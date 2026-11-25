using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

public sealed record ReadPage(
	string Title,
	string Url,
	string Text,
	string? Error = null,
	string? FinalUrl = null,
	IReadOnlyList<string>? RedirectChain = null,
	string? SecuritySummary = null)
{
	public bool Success => Error is null && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Reads public HTML using a script-free HTTP client. Every DNS answer is checked
/// and the validated address is used for the actual socket connection to prevent
/// DNS rebinding between validation and connect.
/// </summary>
public sealed class WebReaderService
{
	private const int MaxBytes = 1_500_000;
	private const int MaxTextChars = 8500;
	private const int MinUsefulChars = 35;
	private const int MaxLinksPerRequest = 100;
	private const int MaxRedirects = 5;
	private static readonly SocketsHttpHandler Handler = new()
	{
		AllowAutoRedirect = false,
		AutomaticDecompression = DecompressionMethods.All,
		UseProxy = false,
		MaxConnectionsPerServer = 4,
		ConnectCallback = ConnectToValidatedAddressAsync
	};
	private static readonly HttpClient Reader = new(Handler) { Timeout = TimeSpan.FromSeconds(15) };

	/// <summary>Attempts every supplied URL and returns one result per URL, including failures.</summary>
	public async Task<List<ReadPage>> ReadAsync(IEnumerable<SearchResult> results, int maxPages = 3, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
	{
		var requested = results
			.DistinctBy(result => result.Url, StringComparer.OrdinalIgnoreCase)
			.Take(Math.Min(Math.Max(maxPages, 1), MaxLinksPerRequest + 1))
			.ToList();
		if (requested.Count > MaxLinksPerRequest)
			throw new InvalidOperationException($"本次來源超過 {MaxLinksPerRequest} 個連結，尚未逐一讀完；請分批讀取，不會標記為完成。");

		var output = new ReadPage[requested.Count];
		int completed = 0;
		using var throttle = new SemaphoreSlim(4, 4);
		await Task.WhenAll(Enumerable.Range(0, requested.Count).Select(async index =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			var result = requested[index];
			await throttle.WaitAsync(cancellationToken);
			progress?.Report($"安全讀取提供的來源連結 {Volatile.Read(ref completed) + 1}/{requested.Count}：{result.Title}");
			try
			{
				var downloaded = await DownloadHtmlAsync(result.Url, cancellationToken);
				string content = ExtractText(downloaded.Html);
				if (content.Length < MinUsefulChars)
				{
					output[index] = new ReadPage(result.Title, result.Url, content, "安全擷取完成，但正文少於可用長度；不會執行 JavaScript 以補抓動態內容。",
						downloaded.FinalUrl, downloaded.Redirects, BuildSecuritySummary(downloaded.FinalUri, downloaded.Redirects, downloaded.Headers, downloaded.Html));
					return;
				}
				output[index] = new ReadPage(result.Title, result.Url, content, null, downloaded.FinalUrl, downloaded.Redirects,
					BuildSecuritySummary(downloaded.FinalUri, downloaded.Redirects, downloaded.Headers, downloaded.Html));
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				output[index] = new ReadPage(result.Title, result.Url, "", "安全讀取失敗：" + SafeError(ex));
			}
			finally { throttle.Release(); Interlocked.Increment(ref completed); }
		}));
		progress?.Report($"已逐一嘗試讀取 {output.Length}/{requested.Count} 個來源連結；讀取失敗 {output.Count(page => !page.Success)} 個。");
		return output.ToList();
	}

	private static async ValueTask<Stream> ConnectToValidatedAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
	{
		string host = context.DnsEndPoint.Host.TrimEnd('.');
		if (IsBlockedHost(host)) throw new InvalidOperationException("不允許連線至本機、保留或特殊用途網域。");
		IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
			? new[] { literal }
			: await Dns.GetHostAddressesAsync(host, cancellationToken);
		if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
			throw new InvalidOperationException("網域解析到非公開或混合的內部網路位址，已封鎖連線。");

		Exception? lastError = null;
		foreach (var address in addresses)
		{
			var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
			try
			{
				await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
				return new NetworkStream(socket, ownsSocket: true);
			}
			catch (Exception ex) when (ex is SocketException or OperationCanceledException)
			{
				socket.Dispose();
				if (ex is OperationCanceledException) throw;
				lastError = ex;
			}
		}
		throw new IOException("無法連線到已驗證的公開網域。", lastError);
	}

	private static async Task<DownloadedPage> DownloadHtmlAsync(string input, CancellationToken cancellationToken)
	{
		if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) throw new InvalidOperationException("來源不是有效的絕對網址。");
		var chain = new List<string> { uri.AbsoluteUri };
		for (int redirect = 0; redirect <= MaxRedirects; redirect++)
		{
			ValidateUriShape(uri);
			using var request = new HttpRequestMessage(HttpMethod.Get, uri);
			request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AsuisuiLLM/1.0; safe passive reader)");
			request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
			using var response = await Reader.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
			{
				if (redirect == MaxRedirects) throw new InvalidOperationException($"重新導向超過 {MaxRedirects} 次。");
				var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
				ValidateUriShape(next);
				if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
					throw new InvalidOperationException("重新導向嘗試從 HTTPS 降級至 HTTP，已封鎖。");
				uri = next;
				chain.Add(uri.AbsoluteUri);
				continue;
			}

			response.EnsureSuccessStatusCode();
			string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
			if (!mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
				!mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("來源不是 HTML/XHTML，已拒絕下載非網頁內容。");
			if (response.Content.Headers.ContentLength is > MaxBytes)
				throw new InvalidOperationException($"網頁超過 {MaxBytes} bytes 上限。");
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			using var buffer = new MemoryStream();
			var bytes = new byte[8192];
			int count;
			while ((count = await stream.ReadAsync(bytes.AsMemory(), cancellationToken)) > 0)
			{
				if (buffer.Length + count > MaxBytes) throw new InvalidOperationException($"網頁超過 {MaxBytes} bytes 上限。");
				buffer.Write(bytes, 0, count);
			}
			var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
			Encoding encoding;
			try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
			catch { encoding = Encoding.UTF8; }
			var headers = response.Headers.Concat(response.Content.Headers)
				.ToDictionary(item => item.Key, item => string.Join(", ", item.Value), StringComparer.OrdinalIgnoreCase);
			return new DownloadedPage(encoding.GetString(buffer.ToArray()), uri, chain, headers);
		}
		throw new InvalidOperationException("重新導向次數過多。");
	}

	private static void ValidateUriShape(Uri uri)
	{
		if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
			throw new InvalidOperationException("只允許 HTTP 或 HTTPS 網址。");
		if (!string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType == UriHostNameType.Unknown)
			throw new InvalidOperationException("網址格式不安全，已封鎖。");
		if (uri.Port != 80 && uri.Port != 443)
			throw new InvalidOperationException("只允許標準 HTTP/HTTPS 連接埠。");
		if (IsBlockedHost(uri.IdnHost.TrimEnd('.')))
			throw new InvalidOperationException("不允許讀取本機或特殊用途網域。");
		if (uri.Scheme == Uri.UriSchemeHttps && uri.Port != 443 || uri.Scheme == Uri.UriSchemeHttp && uri.Port != 80)
			throw new InvalidOperationException("網址協定與連接埠不相符。");
	}

	private static bool IsBlockedHost(string host) =>
		host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
		new[] { ".localhost", ".local", ".internal", ".home.arpa", ".onion", ".test", ".invalid", ".example", ".arpa" }
			.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
		IPAddress.TryParse(host, out var ip) && !IsPublicAddress(ip);

	private static bool IsPublicAddress(IPAddress address)
	{
		if (IPAddress.IsLoopback(address)) return false;
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		if (address.AddressFamily == AddressFamily.InterNetwork)
		{
			var b = address.GetAddressBytes();
			return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
				!(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
				!(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
				!(b[0] == 192 && b[1] == 168) && !(b[0] == 192 && b[1] == 0) &&
				!(b[0] == 198 && (b[1] == 18 || b[1] == 19)) &&
				!(b[0] == 198 && b[1] == 51 && b[2] == 100) &&
				!(b[0] == 203 && b[1] == 0 && b[2] == 113);
		}
		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			var b = address.GetAddressBytes();
			ushort first = (ushort)((b[0] << 8) | b[1]);
			return first >= 0x2000 && first <= 0x3fff &&
				!(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
		}
		return false;
	}

	private static string BuildSecuritySummary(Uri finalUri, IReadOnlyList<string> redirects, IReadOnlyDictionary<string, string> headers, string html)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);
		var scriptHosts = (doc.DocumentNode.SelectNodes("//script[@src]") ?? Enumerable.Empty<HtmlNode>())
			.Select(node => node.GetAttributeValue("src", ""))
			.Select(value => Uri.TryCreate(finalUri, value, out var script) ? script : null)
			.Where(script => script is not null && (script.Scheme == Uri.UriSchemeHttp || script.Scheme == Uri.UriSchemeHttps))
			.Select(script => script!.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		int formCount = doc.DocumentNode.SelectNodes("//form")?.Count ?? 0;
		var actionHosts = new List<string>();
		int crossDomainActions = 0;
		foreach (var form in doc.DocumentNode.SelectNodes("//form[@action]") ?? Enumerable.Empty<HtmlNode>())
		{
			if (Uri.TryCreate(finalUri, form.GetAttributeValue("action", ""), out var action) &&
				(action.Scheme == Uri.UriSchemeHttp || action.Scheme == Uri.UriSchemeHttps))
			{
				actionHosts.Add(action.IdnHost);
				if (!string.Equals(action.IdnHost, finalUri.IdnHost, StringComparison.OrdinalIgnoreCase)) crossDomainActions++;
			}
		}
		string redirectInfo = redirects.Count > 1
			? $"轉址 {redirects.Count - 1} 次 ({string.Join(" → ", redirects.Select(url => new Uri(url).GetLeftPart(UriPartial.Path)))}；已隱去查詢參數)"
			: "無轉址";
		string tls = finalUri.Scheme == Uri.UriSchemeHttps ? "HTTPS 憑證由系統驗證" : "HTTP 明文連線";
		string headerInfo = string.Join(", ", new[] { "Content-Security-Policy", "Strict-Transport-Security", "X-Content-Type-Options" }
			.Select(name => headers.ContainsKey(name) ? name : null).Where(name => name is not null));
		if (headerInfo.Length == 0) headerInfo = "未見 CSP/HSTS/X-Content-Type-Options";
		string scriptInfo = scriptHosts.Length == 0 ? "無外部 JavaScript" : $"外部 JavaScript 網域 {string.Join(", ", scriptHosts)}（未執行）";
		string actions = formCount == 0 ? "無表單" : actionHosts.Count == 0 ? $"表單 {formCount} 個（無明確送出目標；未互動、未提交）" : $"表單送出網域 {string.Join(", ", actionHosts.Distinct(StringComparer.OrdinalIgnoreCase))}（{crossDomainActions} 個跨網域；未互動、未提交）";
		return $"{tls}；{redirectInfo}；{scriptInfo}；{headerInfo}；{actions}；未呼叫第三方威脅情報服務";
	}

	private static string SafeError(Exception exception)
	{
		string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return message.Length <= 240 ? message : message[..240] + "…";
	}

	private static string ExtractText(string html)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);
		foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//aside|//form|//noscript|//svg|//iframe")?.ToArray() ?? Array.Empty<HtmlNode>()) node.Remove();
		var metadata = new List<string>();
		foreach (var selector in new[] { "//meta[@name='description']", "//meta[@property='og:description']", "//meta[@name='twitter:description']", "//meta[@property='og:title']" })
		{
			var meta = doc.DocumentNode.SelectSingleNode(selector)?.GetAttributeValue("content", "");
			if (!string.IsNullOrWhiteSpace(meta)) metadata.Add(WebUtility.HtmlDecode(meta));
		}
		var candidates = doc.DocumentNode.SelectNodes("//article|//main|//*[@role='main']|//body");
		var best = candidates?.OrderByDescending(node => node.InnerText.Length).FirstOrDefault(node => node.Name != "body")
			?? doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
		var builder = new StringBuilder();
		foreach (var node in (IEnumerable<HtmlNode>?)best.SelectNodes(".//h1|.//h2|.//h3|.//p|.//li|.//td") ?? Enumerable.Empty<HtmlNode>())
		{
			string line = WebUtility.HtmlDecode(node.InnerText);
			line = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			// List items and table cells often contain short but essential values,
			// such as character names. Keep those even when prose is filtered.
			if (line.Length == 0 || (line.Length < 15 && node.Name is not ("li" or "td"))) continue;
			if (builder.Length + line.Length > MaxTextChars) break;
			builder.AppendLine(line);
		}
		string body = builder.ToString().Trim();
		string combined = string.Join("\n", metadata.Distinct()) + "\n" + body;
		combined = combined.Trim();
		return combined[..Math.Min(combined.Length, MaxTextChars)];
	}

	private sealed record DownloadedPage(string Html, Uri FinalUri, IReadOnlyList<string> Redirects, IReadOnlyDictionary<string, string> Headers)
	{
		public string FinalUrl => FinalUri.AbsoluteUri;
	}
}
