
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

using var client = new HttpClient
{
	Timeout = TimeSpan.FromMinutes(2)
};

var memory = new MemoryService();

var messages = new List<ChatMessage>
{
	new("system", """
        你是一位親切、自然的日語 AI 夥伴。
        使用者可以使用繁體中文或日文與你聊天。	

        規則：
        1. 永遠先用自然的日文回答。
        2. 下一行提供繁體中文翻譯。
        3. 日文程度約 N4～N3。
        4. 使用朋友般的語氣。
        5. 發現日文錯誤時，簡短協助修正。
        6. 回覆簡短，除非使用者要求詳細說明。
        7. 回答個人資訊時，優先參考已儲存的記憶。
        8. 不知道的事情不要猜測。
        9. 不要輸出分析、思考過程或規則說明。

        回答格式：
        日本語：こんにちは！
        中文：你好！
        		你只能輸出最終回答。
        禁止輸出分析、推理、規則檢查、思考步驟。
        禁止使用 <think> 標籤。
        每次回答最多兩行。

        正確範例：
        日本語：りんごが好きなんだね！
        中文：原來你喜歡蘋果呀！
        """)
};

Console.WriteLine("AI 已啟動！");
Console.WriteLine("輸入 exit 結束。");
Console.WriteLine("輸入 /remember 名稱=內容 儲存記憶。");
Console.WriteLine("輸入 /memories 查看記憶。");

while (true)
{
	Console.Write("\n你：");

	string? input = Console.ReadLine();

	if (string.IsNullOrWhiteSpace(input))
		continue;

	input = input.Trim();

	if (input.Equals(
		"exit",
		StringComparison.OrdinalIgnoreCase))
	{
		break;
	}

	// 手動儲存永久記憶
	if (input.StartsWith(
		"/remember ",
		StringComparison.OrdinalIgnoreCase))
	{
		string data = input["/remember ".Length..];
		int separator = data.IndexOf('=');

		if (separator <= 0 || separator == data.Length - 1)
		{
			Console.WriteLine("格式：/remember 名稱=內容");
			continue;
		}

		string key = data[..separator].Trim();
		string value = data[(separator + 1)..].Trim();

		if (key.Length == 0 || value.Length == 0)
		{
			Console.WriteLine("名稱和內容不可為空。");
			continue;
		}

		try
		{
			memory.Save(key, value);
			Console.WriteLine("記憶已儲存！");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"儲存失敗：{ex.Message}");
		}

		continue;
	}

	// 查看永久記憶
	if (input.Equals(
		"/memories",
		StringComparison.OrdinalIgnoreCase))
	{
		try
		{
			string saved = memory.GetAll();

			Console.WriteLine(
				string.IsNullOrWhiteSpace(saved)
					? "目前沒有永久記憶。"
					: $"\n永久記憶：\n{saved}"
			);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"讀取失敗：{ex.Message}");
		}

		continue;
	}

	messages.Add(new ChatMessage("user", input));

	var stopwatch = Stopwatch.StartNew();

	try
	{
		// 讀取 SQLite 記憶
		string savedMemories = memory.GetAll();

		string memoryPrompt =
			string.IsNullOrWhiteSpace(savedMemories)
				? "目前沒有已儲存的長期記憶。"
				: "以下是使用者的長期記憶，回答相關問題時請參考：\n"
				  + savedMemories;

		// 組合系統規則、記憶及聊天紀錄
		var requestMessages = new List<ChatMessage>
		{
			messages[0],
			new("system", memoryPrompt)
		};

		requestMessages.AddRange(messages.Skip(1));

		// 避免聊天歷史無限制增長
		const int maxHistoryMessages = 20;

		if (requestMessages.Count > maxHistoryMessages + 2)
		{
			requestMessages = requestMessages
				.Take(2)
				.Concat(
					requestMessages
						.Skip(2)
						.TakeLast(maxHistoryMessages)
				)
				.ToList();
		}

		var request = new
		{
			model = "qwen3:4b-instruct",
			messages = requestMessages,
			stream = false,
			think = false,
			keep_alive = "30m",
			options = new
			{
				num_predict = 256,
				temperature = 0.2
			}
		};

		// 呼叫 Ollama
		using var response = await client.PostAsJsonAsync(
			"http://localhost:11434/api/chat",
			request
		);

		response.EnsureSuccessStatusCode();

		using var json = JsonDocument.Parse(
			await response.Content.ReadAsStringAsync()
		);

		JsonElement message = json.RootElement
			.GetProperty("message");

		// 僅讀取正式回答，不讀取 thinking 欄位
		string answer = message
			.GetProperty("content")
			.GetString() ?? "";

		// 額外防護：移除混入 content 的思考內容
		answer = RemoveThinking(answer);

		stopwatch.Stop();

		if (string.IsNullOrWhiteSpace(answer))
		{
			Console.WriteLine("\nAI：沒有取得有效回答。");
			messages.RemoveAt(messages.Count - 1);
			continue;
		}

		Console.WriteLine($"\nAI：{answer}");
		Console.WriteLine(
			$"\n[回答耗時：{stopwatch.Elapsed.TotalSeconds:F2} 秒]"
		);

		// 只儲存清理後的正式回答
		messages.Add(new ChatMessage("assistant", answer));
	}
	catch (Exception ex)
	{
		stopwatch.Stop();

		// API 失敗時移除尚未完成的使用者訊息
		messages.RemoveAt(messages.Count - 1);

		Console.WriteLine($"錯誤：{ex.Message}");
	}
}

Console.WriteLine("AI 已結束。");

// 移除 Qwen3 意外輸出的 Thinking 區塊
static string RemoveThinking(string content)
{
	const string startTag = "<think>";
	const string endTag = "</think>";

	// 優先保留最後一個 </think> 後面的正式回答
	int endIndex = content.LastIndexOf(
		endTag,
		StringComparison.OrdinalIgnoreCase
	);

	if (endIndex >= 0)
	{
		return content[
			(endIndex + endTag.Length)..
		].Trim();
	}

	// 如果只有 <think> 而沒有結束標記，
	// 避免將未完成的推理內容顯示出來
	int startIndex = content.IndexOf(
		startTag,
		StringComparison.OrdinalIgnoreCase
	);

	if (startIndex >= 0)
	{
		return content[..startIndex].Trim();
	}

	return content.Trim();
}

record ChatMessage(string role, string content);
