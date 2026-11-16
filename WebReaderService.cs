using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

public sealed record ReadPage(string Title, string Url, string Text, string? Error = null)
{
    public bool Success => Error is null && !string.IsNullOrWhiteSpace(Text);
}

// 讀取公開網頁的正文。此服務不執行 JavaScript，也不嘗試繞過付費牆或登入。
public sealed class WebReaderService
{
    private static readonly HttpClientHandler Handler = new() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
    private static readonly HttpClient Reader = new(Handler) { Timeout = TimeSpan.FromSeconds(12) };
    private const int MaxBytes = 1_500_000;
    private const int MaxTextChars = 8500;

    public async Task<List<ReadPage>> ReadAsync(IEnumerable<SearchResult> results, int maxPages = 3, CancellationToken cancellationToken = default)
    {
        var output = new List<ReadPage>();
        foreach (var result in results.Take(Math.Clamp(maxPages, 1, 5)))
        {
            try
            {
                string html = await DownloadHtmlAsync(result.Url, cancellationToken);
                string content = ExtractText(html);
                output.Add(new ReadPage(result.Title, result.Url, content,
                    content.Length < 100 ? "頁面沒有足夠的可讀正文（可能需要 JavaScript 或登入）" : null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                output.Add(new ReadPage(result.Title, result.Url, "", ex.Message));
            }
        }
        return output;
    }

    private static async Task<string> DownloadHtmlAsync(string input, CancellationToken ct)
    {
        var uri = new Uri(input);
        for (int redirect = 0; redirect <= 3; redirect++)
        {
            await ValidatePublicUriAsync(uri, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AsuisuiLLM/1.0; personal reader)");
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await Reader.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("非 HTML 網頁，已略過");
            if (response.Content.Headers.ContentLength is > MaxBytes)
                throw new InvalidOperationException("網頁過大，已略過");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new System.IO.MemoryStream();
            var bytes = new byte[8192];
            int n;
            while ((n = await stream.ReadAsync(bytes.AsMemory(), ct)) > 0)
            {
                if (buffer.Length + n > MaxBytes) throw new InvalidOperationException("網頁超過大小限制");
                buffer.Write(bytes, 0, n);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch { encoding = Encoding.UTF8; }
            return encoding.GetString(buffer.ToArray());
        }
        throw new InvalidOperationException("網頁重新導向次數過多");
    }

    private static async Task ValidatePublicUriAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("不支援的網址協定");
        if (uri.UserInfo.Length > 0 || uri.HostNameType == UriHostNameType.Unknown)
            throw new InvalidOperationException("不安全的網址");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不允許讀取本機網頁");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublicAddress(ip)))
            throw new InvalidOperationException("不允許讀取內部網路位址");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
                   !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
                   !(b[0] == 169 && b[1] == 254) &&
                   !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
                   !(b[0] == 192 && b[1] == 168) &&
                   !(b[0] == 192 && b[1] == 0) &&
                   !(b[0] == 198 && (b[1] == 18 || b[1] == 19)) &&
                   !(b[0] == 192 && b[1] == 0 && b[2] == 2) &&
                   !(b[0] == 198 && b[1] == 51 && b[2] == 100) &&
                   !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast &&
                   !address.Equals(IPAddress.IPv6None) && !address.Equals(IPAddress.IPv6Any) &&
                   !(b[0] == 0xfc || b[0] == 0xfd) && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        }
        return false;
    }

    private static string ExtractText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//aside|//form|//noscript|//svg|//iframe")?.ToArray() ?? Array.Empty<HtmlNode>())
            node.Remove();
        var candidates = doc.DocumentNode.SelectNodes("//article|//main|//*[@role='main']|//body");
        var best = candidates?.OrderByDescending(x => x.InnerText.Length).FirstOrDefault(x => x.Name != "body")
                   ?? doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        var sb = new StringBuilder();
        foreach (var node in (IEnumerable<HtmlNode>?)best.SelectNodes(".//h1|.//h2|.//h3|.//p|.//li|.//td") ?? Enumerable.Empty<HtmlNode>())
        {
            string line = WebUtility.HtmlDecode(node.InnerText);
            line = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length < 15) continue;
            if (sb.Length + line.Length > MaxTextChars) break;
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }
}
