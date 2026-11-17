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
using Microsoft.Playwright;

public sealed record ReadPage(string Title, string Url, string Text, string? Error = null)
{
    public bool Success => Error is null && !string.IsNullOrWhiteSpace(Text);
}

// 讀取公開網頁：先嘗試 HTTP，必要時使用 Playwright 背景 Chrome。
public sealed class WebReaderService
{
    private static readonly HttpClientHandler Handler = new() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
    private static readonly HttpClient Reader = new(Handler) { Timeout = TimeSpan.FromSeconds(12) };
    private const int MaxBytes = 1_500_000;
    private const int MaxTextChars = 8500;
    private const int MinUsefulChars = 35;
    private const int MaxBrowserFallbacks = 3;

    public async Task<List<ReadPage>> ReadAsync(IEnumerable<SearchResult> results, int maxPages = 3, CancellationToken cancellationToken = default)
    {
        var output = new List<ReadPage>();
        var pending = new List<(int Index, SearchResult Result)>();
        foreach (var result in results.Take(Math.Clamp(maxPages, 1, 5)))
        {
            string content = "";
            string? error = null;
            try
            {
                string html = await DownloadHtmlAsync(result.Url, cancellationToken);
                content = ExtractText(html);
                if (content.Length < MinUsefulChars)
                    error = "HTTP 擷取內容不足，等待 Chrome 背景閱讀";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                error = "HTTP：" + ex.Message;
            }
            output.Add(new ReadPage(result.Title, result.Url, content, error));
            if (error != null) pending.Add((output.Count - 1, result));
        }

        if (pending.Count == 0) return output;

        // 一次只啟動一個背景 Chrome context，重用於多個網頁；不與搜尋器的 persistent profile 競爭。
        try
        {
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "chrome", Headless = true,
                Args = new[] { "--disable-background-networking" }
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "zh-TW", ServiceWorkers = ServiceWorkerPolicy.Block
            });
            try
            {
                int attempts = 0;
                foreach (var (index, result) in pending)
                {
                    if (attempts++ >= MaxBrowserFallbacks) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        string content = await ReadWithChromeAsync(context, result.Url, cancellationToken);
                        if (content.Length >= MinUsefulChars)
                            output[index] = new ReadPage(result.Title, result.Url, content);
                        else
                            output[index] = new ReadPage(result.Title, result.Url, content,
                                "Chrome 已載入，但仍無法取得有用文字（可能需要登入、驗證或網站限制）");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        var old = output[index];
                        output[index] = old with { Error = old.Error + "；Chrome：" + ex.Message };
                    }
                }
            }
            finally { await context.CloseAsync(); await browser.CloseAsync(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"Chrome 背景閱讀無法啟動：{ex.Message}");
        }
        return output;
    }

    private static async Task<string> ReadWithChromeAsync(IBrowserContext context, string url, CancellationToken ct)
    {
        await ValidatePublicUriAsync(new Uri(url), ct);
        var page = await context.NewPageAsync();
        try
        {
            // 阻止讀取本機或私有位址，並減少不必要的圖片、影音下載。
            await page.RouteAsync("**/*", async route =>
            {
                var req = route.Request;
                if (req.ResourceType is "image" or "media" or "font")
                {
                    await route.AbortAsync();
                    return;
                }
                if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") ||
                    IsLocalOrPrivateHost(uri.Host))
                {
                    await route.AbortAsync();
                    return;
                }
                await route.ContinueAsync();
            });
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000
            });
            // 不等待 networkidle：YouTube / Reddit 常有持續連線。
            await page.WaitForTimeoutAsync(1300);
            ct.ThrowIfCancellationRequested();
            string text = await page.EvaluateAsync<string>(@"() => {
                const clean = s => (s || '').replace(/\s+/g, ' ').trim();
                const pieces = [];
                const add = s => { s = clean(s); if(s && !pieces.includes(s)) pieces.push(s); };
                add(document.title);
                for(const sel of ['meta[name=""description""]','meta[property=""og:description""]',
                                  'meta[name=""twitter:description""]','meta[property=""og:title""]']) {
                    const el = document.querySelector(sel); if(el) add(el.content);
                }
                const root = document.querySelector('article, main, [role=""main""]') || document.body;
                if(root) {
                    const clone = root.cloneNode(true);
                    clone.querySelectorAll('script,style,nav,footer,header,aside,form,svg,iframe').forEach(x => x.remove());
                    add(clone.innerText || clone.textContent);
                }
                return pieces.join('\n').slice(0, 8500);
            }");
            return CleanExtracted(text);
        }
        finally { await page.CloseAsync(); }
    }

    private static bool IsLocalOrPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && !IsPublicAddress(ip);
    }

    private static string CleanExtracted(string text)
    {
        var lines = text.Split('\n').Select(x => string.Join(" ", x.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries))).Where(x => x.Length > 0);
        string cleaned = string.Join("\n", lines).Trim();
        return cleaned[..Math.Min(cleaned.Length, MaxTextChars)];
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
        var metadata = new List<string>();
        foreach (var selector in new[] { "//meta[@name='description']", "//meta[@property='og:description']",
            "//meta[@name='twitter:description']", "//meta[@property='og:title']" })
        {
            var meta = doc.DocumentNode.SelectSingleNode(selector)?.GetAttributeValue("content", "");
            if (!string.IsNullOrWhiteSpace(meta)) metadata.Add(WebUtility.HtmlDecode(meta));
        }
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
        string body = sb.ToString().Trim();
        string combined = string.Join("\n", metadata.Distinct()) + "\n" + body;
        return combined.Trim()[..Math.Min(combined.Trim().Length, MaxTextChars)];
    }
}
