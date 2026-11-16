
using System.Net.Http.Json;
using System.Text.Json;

using var client = new HttpClient();

Console.Write("你：");
string input = Console.ReadLine() ?? "";

var request = new
{
	model = "qwen3:4b",

	messages = new[]
   {
	   new
	   {
		   role = "system",
		   content = """  
           你是一位親切、自然的日語 AI 夥伴。  

           規則：  
           1. 使用者可以用繁體中文或日文與你對話。  
           2. 無論使用者使用哪種語言，你都要先用日文回答。  
           3. 日文回答下方附上繁體中文翻譯。  
           4. 使用自然、生活化的日文，程度約 N4～N3。  
           5. 如果使用者的日文有錯誤，溫柔地提供正確說法。  
           6. 保持朋友般的語氣，不要過度正式。  
           7. 回覆簡短，除非使用者要求詳細說明。  

           回覆格式：  
           日本語：こんにちは！今日は何をする？  
           中文：你好！今天要做什麼呢？  
           """
	   },
	   new { role = "user", content = input }
   },
	stream = false
};

var response = await client.PostAsJsonAsync(
    "http://localhost:11434/api/chat",
    request
);

response.EnsureSuccessStatusCode();

using var json = JsonDocument.Parse(
    await response.Content.ReadAsStringAsync()
);

string answer = json.RootElement
    .GetProperty("message")
    .GetProperty("content")
    .GetString() ?? "";

Console.WriteLine($"AI：{answer}");
