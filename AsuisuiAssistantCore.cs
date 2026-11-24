using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

public enum AssistantMode { Local, GoogleAi, ChatGpt, Web }

public sealed record AssistantSource(string Title, string Url);
public sealed record AssistantResponse(string Answer, IReadOnlyList<AssistantSource> Sources, bool IsExternal);

/// <summary>Application-facing assistant API. It owns conversation state and returns data, never console text.</summary>
public sealed class AsuisuiAssistantCore : IDisposable
{
	private const string SystemPrompt = """
		你是一位親切、自然的日語 AI 夥伴，使用者使用繁體中文或日文。
		一般聊天請先以自然的 N4～N3 日文回答，下一行給繁體中文翻譯。
		不要輸出思考過程或 <think> 標籤。個人資訊只可依實際保存的記憶回答；不知道就直接說不知道。
		""";
	private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
	private readonly MemoryService memory = new();
	private readonly GoogleAiModeSearchService google = new();
	private readonly WebSearchService webSearch;
	private readonly WebReaderService webReader = new();
	private readonly GoogleProfileSettings profileSettings = new();
	private readonly List<AssistantChatMessage> history = new();

	public AsuisuiAssistantCore()
	{
		webSearch = new WebSearchService(client);
		if (!string.IsNullOrWhiteSpace(profileSettings.SelectedPath)) google.SetProfilePath(profileSettings.SelectedPath);
	}

	public string GetMemories() => memory.GetAll();
	public IReadOnlyList<ChromeProfileOption> GetChromeProfiles() => ChromeProfileSettings.GetProfiles();
	public void SetChromeProfile(string path) { google.SetProfilePath(path); profileSettings.SelectedPath = path; }
	public void ClearConversation() => history.Clear();

	public async Task<AssistantResponse> AskAsync(string question, AssistantMode mode, byte[]? image = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("請輸入訊息。", nameof(question));
		question = question.Trim();
		if (question.StartsWith("/memories", StringComparison.OrdinalIgnoreCase))
			return new AssistantResponse(string.IsNullOrWhiteSpace(GetMemories()) ? "目前沒有已儲存的永久記憶。" : GetMemories(), Array.Empty<AssistantSource>(), false);
		if (question.StartsWith("/remember ", StringComparison.OrdinalIgnoreCase))
		{
			string value = question[10..]; int separator = value.IndexOf('=');
			if (separator <= 0 || separator == value.Length - 1) throw new ArgumentException("格式：/remember 名稱=內容");
			memory.Save(value[..separator].Trim(), value[(separator + 1)..].Trim());
			return new AssistantResponse("記憶已儲存。", Array.Empty<AssistantSource>(), false);
		}
		if (image is not null || mode is AssistantMode.GoogleAi or AssistantMode.ChatGpt)
			return await AskExternalAsync(question, image, mode, progress, cancellationToken);
		if (mode == AssistantMode.Web) return await AskWebAsync(question, progress, cancellationToken);
		return await AskLocalAsync(question, progress, cancellationToken);
	}

	private async Task<AssistantResponse> AskExternalAsync(string question, byte[]? image, AssistantMode mode, IProgress<string>? progress, CancellationToken cancellationToken)
	{
		if (image is not null)
		{
			progress?.Report("正在將圖片傳給 Google AI…");
			var result = await google.SearchImageAsync(question, image, cancellationToken);
			return ToExternalResponse(result, "Google AI 未傳回可讀取的圖片分析結果。");
		}
		progress?.Report(mode == AssistantMode.ChatGpt ? "正在詢問 ChatGPT…" : "正在詢問 Google AI…");
		var answer = mode == AssistantMode.ChatGpt
			? await google.SearchChatGptAsync(question, cancellationToken)
			: await google.SearchAsync(question, cancellationToken);
		return ToExternalResponse(answer, "外部 AI 未傳回可讀取的回答。");
	}

	private static AssistantResponse ToExternalResponse(GoogleAiModeResult result, string unavailable)
	{
		string answer = result.Answer.Trim();
		if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException(unavailable);
		return new AssistantResponse(answer, result.Sources.Select(x => new AssistantSource(x.Title, x.Url)).ToList(), true);
	}

	private async Task<AssistantResponse> AskWebAsync(string question, IProgress<string>? progress, CancellationToken cancellationToken)
	{
		progress?.Report("正在搜尋網頁並讀取來源…");
		var results = await webSearch.SearchAsync(question, 5, cancellationToken);
		if (results.Count == 0) throw new InvalidOperationException("沒有找到可用的網頁搜尋結果。");
		var pages = (await webReader.ReadAsync(results, 5, cancellationToken)).Where(page => page.Success).ToList();
		if (pages.Count == 0) throw new InvalidOperationException("搜尋結果的網頁正文都無法讀取。");
		var evidence = new StringBuilder("以下是程式實際讀到的網頁正文。只根據它回答；不足時直接說無法確認。\n");
		foreach (var page in pages)
			evidence.AppendLine($"\n【{page.Title}】\n網址：{page.Url}\n{page.Text[..Math.Min(page.Text.Length, 4000)]}");
		var answer = await AskLocalAsync(question, progress, cancellationToken, evidence.ToString());
		return answer with { Sources = pages.Select(page => new AssistantSource(page.Title, page.Url)).ToList() };
	}

	private async Task<AssistantResponse> AskLocalAsync(string question, IProgress<string>? progress, CancellationToken cancellationToken, string? evidence = null)
	{
		progress?.Report("正在使用本機模型思考…");
		string saved = memory.GetAll();
		var messages = new List<object>
		{
			new { role = "system", content = SystemPrompt },
			new { role = "system", content = string.IsNullOrWhiteSpace(saved) ? "目前沒有已儲存的長期記憶。" : "使用者的長期記憶：\n" + saved }
		};
		messages.AddRange(history.TakeLast(12).Select(x => new { role = x.Role, content = x.Content }));
		if (!string.IsNullOrWhiteSpace(evidence)) messages.Add(new { role = "system", content = evidence });
		messages.Add(new { role = "user", content = question });
		var request = new { model = "qwen3:4b-instruct", messages, stream = false, think = false, keep_alive = "30m", options = new { num_ctx = 8192, num_predict = 2048, temperature = 0.2 } };
		using var response = await client.PostAsJsonAsync("http://localhost:11434/api/chat", request, cancellationToken);
		string body = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException($"本機模型無法回應（HTTP {(int)response.StatusCode}）：{body}");
		using var json = JsonDocument.Parse(body);
		string answer = RemoveThinking(json.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "");
		if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("本機模型沒有回傳有效回答。");
		history.Add(new AssistantChatMessage("user", question));
		history.Add(new AssistantChatMessage("assistant", answer));
		return new AssistantResponse(answer, Array.Empty<AssistantSource>(), false);
	}

	private static string RemoveThinking(string content)
	{
		int end = content.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
		if (end >= 0) return content[(end + 8)..].Trim();
		int start = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
		return start >= 0 ? content[..start].Trim() : content.Trim();
	}

	public void Dispose() { google.Dispose(); client.Dispose(); }
	private sealed record AssistantChatMessage(string Role, string Content);
}
