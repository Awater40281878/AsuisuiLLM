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

// 使用獨立的背景 Chrome context 搜尋，不建立或操作使用者目前的視窗。
public sealed class WebSearchService
{
	private const string SearchEndpoint = "https://duckduckgo.com/?q=";
	private const string BingSearchEndpoint = "https://www.bing.com/search?q=";
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

			// 使用一般 Chrome，啟動時要求最小化。
			await using (var background = await OpenBrowserAsync(playwright, profile))
			{
				var page = background.Pages.FirstOrDefault() ?? await background.NewPageAsync();
				await NavigateAsync(page, url);
				var results = await WaitForResultsAsync(page, maxResults,
					TimeSpan.FromSeconds(8), cancellationToken);
				if (results.Count > 0) return results;

				string html = await page.ContentAsync();
				if (IsNoResults(html)) return results;
				if (!IsChallenge(html, page.Url))
				{
					var fallback = await SearchBingAsync(page, query, maxResults, cancellationToken);
					if (fallback.Count > 0) return fallback;
					throw new InvalidOperationException("DuckDuckGo 與備援搜尋頁都沒有可解析的結果。");
				}

				Console.WriteLine("DuckDuckGo 出現驗證頁，背景 Chrome 等待自動完成……");
				// 某些 JavaScript 驗證會自行跳轉。保留同一個 page / cookie 等待它完成。
				results = await WaitForResultsAsync(page, maxResults,
					TimeSpan.FromSeconds(18), cancellationToken);
				if (results.Count > 0) return results;

				// 驗證有時只更新 Cookie，不會自動返回搜尋結果頁。
				Console.WriteLine("驗證尚未取得結果，使用相同 Chrome 設定檔重試一次……");
				await NavigateAsync(page, url);
				results = await WaitForResultsAsync(page, maxResults,
					TimeSpan.FromSeconds(10), cancellationToken);
				if (results.Count > 0) return results;

				html = await page.ContentAsync();
				if (IsNoResults(html)) return results;
				if (IsChallenge(html, page.Url))
				{
					Console.WriteLine("DuckDuckGo 驗證未解除，切換至 Bing 備援搜尋……");
					var fallback = await SearchBingAsync(page, query, maxResults, cancellationToken);
					if (fallback.Count > 0) return fallback;
					throw new InvalidOperationException("DuckDuckGo 要求人機驗證，且 Bing 備援搜尋未取得可解析結果。");
				}
				throw new InvalidOperationException("重試後仍無法解析 DuckDuckGo 搜尋結果。");
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

	private static async Task<List<SearchResult>> SearchBingAsync(IPage page, string query, int maxResults, CancellationToken cancellationToken)
	{
		await NavigateAsync(page, BingSearchEndpoint + Uri.EscapeDataString(query));
		DateTime deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var results = ParseBingResults(await page.ContentAsync(), maxResults);
			if (results.Count > 0) return results;
			await Task.Delay(600, cancellationToken);
		}
		return new List<SearchResult>();
	}

	private static Task<IBrowserContext> OpenBrowserAsync(
		IPlaywright playwright, string profile) =>
		playwright.Chromium.LaunchPersistentContextAsync(
			profile,
			new BrowserTypeLaunchPersistentContextOptions
			{
				Channel = "chrome", // 使用已安裝的 Google Chrome
				Headless = false,
				Args = new[]
				{
					"--start-minimized",
					"--window-position=-32000,-32000",
					"--window-size=1280,850"
				},
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

	private static List<SearchResult> ParseBingResults(string html, int maxResults)
	{
		var document = new HtmlDocument();
		document.LoadHtml(html);
		var anchors = document.DocumentNode.SelectNodes("//li[@class='b_algo']//h2/a|//ol[@id='b_results']//h2/a");
		var results = new List<SearchResult>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var anchor in anchors ?? Enumerable.Empty<HtmlNode>())
		{
			string target = WebUtility.HtmlDecode(anchor.GetAttributeValue("href", ""));
			string title = CleanText(anchor.InnerText);
			if (title.Length == 0 || !IsHttpUrl(target) || !seen.Add(target)) continue;
			var container = anchor.Ancestors("li").FirstOrDefault(node => node.GetAttributeValue("class", "").Contains("b_algo", StringComparison.Ordinal));
			results.Add(new SearchResult { Title = title, Url = target, Content = CleanText(container?.InnerText ?? ""), Engine = "bing" });
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
