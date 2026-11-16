using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Playwright;

// 預設使用背景 Chrome。只有偵測到驗證頁才顯示 Chrome，
// 驗證後自動讀取搜尋結果並關閉視窗，不需要按 Enter。
public sealed class WebSearchService
{
    private const string SearchEndpoint = "https://duckduckgo.com/?q=";
    private static readonly SemaphoreSlim BrowserLock = new(1, 1);
    private readonly HttpClient _client;

    public WebSearchService(HttpClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<List<SearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("搜尋關鍵字不可為空。", nameof(query));
        if (maxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        string url = SearchEndpoint + Uri.EscapeDataString(query.Trim());
        string profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsuisuiLLM", "DuckDuckGoBrowser");
        Directory.CreateDirectory(profile);

        await BrowserLock.WaitAsync(cancellationToken);
        try
        {
            using var playwright = await Playwright.CreateAsync();

            // 第一階段：背景執行，不顯示任何視窗。
            await using (var background = await OpenBrowserAsync(playwright, profile, visible: false))
            {
                var page = background.Pages.FirstOrDefault() ?? await background.NewPageAsync();
                await NavigateAsync(page, url);
                var results = await WaitForResultsAsync(page, maxResults,
                    TimeSpan.FromSeconds(8), cancellationToken);
                if (results.Count > 0) return results;

                string html = await page.ContentAsync();
                if (IsNoResults(html)) return results;
                if (!IsChallenge(html, page.Url))
                    throw new InvalidOperationException(
                        "搜尋頁面沒有可解析的結果，且未偵測到 CAPTCHA。可能是 DuckDuckGo 頁面結構改變。");
            }

            // 前一個背景瀏覽器已關閉，持久化設定檔才能由可見 Chrome 使用。
            Console.WriteLine("DuckDuckGo 要求人機驗證，已開啟 Chrome，請手動完成驗證。");
            Console.WriteLine("驗證後會自動擷取結果並關閉 Chrome，不必按 Enter（最多等待 2 分鐘）。");
            await using (var visible = await OpenBrowserAsync(playwright, profile, visible: true))
            {
                var page = visible.Pages.FirstOrDefault() ?? await visible.NewPageAsync();
                await NavigateAsync(page, url);

                // 使用者完成 CAPTCHA 後，頁面可能自動跳回結果頁。
                var results = await WaitForResultsAsync(page, maxResults,
                    TimeSpan.FromMinutes(2), cancellationToken);
                if (results.Count > 0) return results;
                if (page.IsClosed)
                    throw new InvalidOperationException("Chrome 視窗已被關閉，無法取得搜尋結果。");

                // 驗證可能只更新 Cookie，未自動回到結果頁：僅重試一次。
                Console.WriteLine("尚未偵測到搜尋結果，正在使用驗證後的瀏覽器重試一次……");
                await NavigateAsync(page, url);
                results = await WaitForResultsAsync(page, maxResults,
                    TimeSpan.FromSeconds(10), cancellationToken);
                if (results.Count > 0) return results;

                string html = await page.ContentAsync();
                if (IsNoResults(html)) return results;
                if (IsChallenge(html, page.Url))
                    throw new InvalidOperationException("DuckDuckGo 仍要求 CAPTCHA，已停止重試。");
                throw new InvalidOperationException("驗證後仍無法解析 DuckDuckGo 搜尋結果。");
            }
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                "Chrome 操作失敗，請確認已安裝 Google Chrome，且 Playwright 可正常啟動：" + ex.Message, ex);
        }
        finally
        {
            BrowserLock.Release();
        }
    }

    private static Task<IBrowserContext> OpenBrowserAsync(
        IPlaywright playwright, string profile, bool visible) =>
        playwright.Chromium.LaunchPersistentContextAsync(
            profile,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Channel = "chrome", // 使用已安裝的 Google Chrome
                Headless = !visible,
                ViewportSize = new ViewportSize { Width = 1280, Height = 850 }
            });

    private static async Task NavigateAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30000
        });
    }

    private static async Task<List<SearchResult>> WaitForResultsAsync(
        IPage page, int maxResults, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed) return new List<SearchResult>();
            try
            {
                string html = await page.ContentAsync();
                var results = ParseResults(html, maxResults);
                if (results.Count > 0 || IsNoResults(html)) return results;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
                // 驗證後導頁時 DOM 可能短暫不可讀，等待下一次檢查。
            }
            await Task.Delay(700, cancellationToken);
        }
        return new List<SearchResult>();
    }

    private static List<SearchResult> ParseResults(string html, int maxResults)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        // 同時支援 DuckDuckGo 常見的新舊結果頁面結構。
        var anchors = document.DocumentNode.SelectNodes(
            "//a[@data-testid='result-title-a' or " +
            "contains(concat(' ', normalize-space(@class), ' '), ' result__a ') or " +
            "(parent::h2 and ancestor::*[@data-testid='result'])]");
        var results = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (anchors == null) return results;

        foreach (var anchor in anchors)
        {
            string title = CleanText(anchor.InnerText);
            string target = ResolveResultUrl(WebUtility.HtmlDecode(anchor.GetAttributeValue("href", "")));
            if (title.Length == 0 || !IsHttpUrl(target) || !seen.Add(target)) continue;

            var container = anchor.Ancestors().FirstOrDefault(node =>
                node.GetAttributeValue("data-testid", "") == "result" ||
                node.GetAttributeValue("class", "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("result"));
            var snippet = container?.SelectSingleNode(
                ".//*[@data-testid='result-snippet' or " +
                "contains(concat(' ', normalize-space(@class), ' '), ' result__snippet ')]");

            results.Add(new SearchResult
            {
                Title = title,
                Url = target,
                Content = CleanText(snippet?.InnerText ?? ""),
                Engine = "duckduckgo"
            });
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    private static bool IsChallenge(string html, string url) =>
        html.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("anomaly", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("verify you are human", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("challenge", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoResults(string html) =>
        html.Contains("No results found", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("No results.", StringComparison.OrdinalIgnoreCase);

    private static string CleanText(string text) =>
        string.Join(" ", WebUtility.HtmlDecode(text)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ResolveResultUrl(string href)
    {
        if (href.StartsWith("//")) href = "https:" + href;
        else if (href.StartsWith("/")) href = "https://duckduckgo.com" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return "";
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/l/", StringComparison.Ordinal))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "uddg")
                    return Uri.UnescapeDataString(parts[1]);
            }
        }
        return uri.ToString();
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

public sealed class SearchResult
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Content { get; set; } = "";
    public string Engine { get; set; } = "";
}
