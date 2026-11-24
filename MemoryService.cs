using Microsoft.Data.Sqlite;

public class MemoryService
{
	private readonly string connectionString;

	public MemoryService()
	{
		string appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsuisuiLLM");
		Directory.CreateDirectory(appDataDirectory);
		string dbPath = Path.Combine(appDataDirectory, "memory.db");
		string legacyPath = Path.Combine(AppContext.BaseDirectory, "memory.db");
		if (!File.Exists(dbPath) && File.Exists(legacyPath) &&
			!string.Equals(Path.GetFullPath(dbPath), Path.GetFullPath(legacyPath), StringComparison.OrdinalIgnoreCase))
			File.Copy(legacyPath, dbPath);
		connectionString = $"Data Source={dbPath}";
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = """
			CREATE TABLE IF NOT EXISTS Memories (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
			CREATE TABLE IF NOT EXISTS KnowledgeMemories (
				Key TEXT PRIMARY KEY, Definition TEXT NOT NULL, Examples TEXT NOT NULL,
				Constraints TEXT NOT NULL, Scope TEXT NOT NULL, Confidence REAL NOT NULL,
				Status TEXT NOT NULL, Source TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
			CREATE TABLE IF NOT EXISTS ConversationArchive (
				Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedUtc TEXT NOT NULL,
				UserText TEXT NOT NULL, AssistantText TEXT NOT NULL, Summary TEXT NOT NULL DEFAULT '');
			CREATE TABLE IF NOT EXISTS VerificationRecords (
				Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedUtc TEXT NOT NULL,
				Claim TEXT NOT NULL, Verdict TEXT NOT NULL, Confidence REAL NOT NULL,
				Reason TEXT NOT NULL, Sources TEXT NOT NULL, Review TEXT NOT NULL DEFAULT '');
			CREATE TABLE IF NOT EXISTS ValidationCases (
				Id INTEGER PRIMARY KEY AUTOINCREMENT, CaseKey TEXT NOT NULL UNIQUE,
				Title TEXT NOT NULL, PromptJson TEXT NOT NULL, ExpectedRubric TEXT NOT NULL,
				CreatedUtc TEXT NOT NULL, SearchRequired INTEGER NOT NULL DEFAULT 0);
			CREATE TABLE IF NOT EXISTS ValidationAttempts (
				Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT NOT NULL,
				CaseId INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, ActualAnswer TEXT NOT NULL,
				Score INTEGER NULL, ReviewerNotes TEXT NOT NULL DEFAULT '', Sources TEXT NOT NULL DEFAULT '');
			""";
		command.ExecuteNonQuery();
		EnsureColumn(connection, "ValidationCases", "SearchRequired", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn(connection, "ValidationAttempts", "Sources", "TEXT NOT NULL DEFAULT ''");
		SeedValidationCases(connection);
	}

	private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
	{
		using var check = connection.CreateCommand();
		check.CommandText = $"PRAGMA table_info({table});";
		using var reader = check.ExecuteReader();
		while (reader.Read()) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
		reader.Close();
		using var alter = connection.CreateCommand();
		alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
		alter.ExecuteNonQuery();
	}

	private static void SeedValidationCases(SqliteConnection connection)
	{
		var cases = new (string Key, string Title, string[] Turns, string Rubric, bool SearchRequired)[]
		{
			("code_filter_even", "C# 偶數篩選程式", new[] { "請用 C# 寫一個函式，輸入整數清單並回傳偶數清單，使用 LINQ，並附一個呼叫範例。" }, "標準答案可為：static List<int> GetEvens(IEnumerable<int> numbers) => numbers.Where(n => n % 2 == 0).ToList(); 呼叫時輸入 [1,2,3,4] 應得到 [2,4]。評分看程式邏輯、LINQ 篩選及呼叫範例；不同但正確的簽名或格式均可接受，不要求逐字一致。", false),
			("japanese_translation", "日文與繁體中文格式", new[] { "請用 N4 左右的自然日文說「我今天想在家休息」，下一行提供繁體中文翻譯。" }, "標準答案：日本語：今日は家で休みたいです。\n中文：我今天想在家休息。評分看日文自然且原意正確，並有繁體中文翻譯；可接受其他語意等價說法，不要求逐字一致。", false),
			("unknown_preference", "未知偏好不臆測（多輪）", new[] { "回答時請只依據我明確告訴你的資訊，不要猜測我的個人偏好。", "你還記得我最喜歡喝什麼飲料嗎？" }, "標準答案意旨：你沒有告訴我最喜歡的飲料，所以我不知道。評分看是否明確承認資訊未知；若猜一種飲料或聲稱記得，即不合格。", false),
			("arithmetic_order", "算式運算順序", new[] { "請計算 2 + 3 × 4，並簡短說明運算順序。" }, "標準答案：14，因為先算乘法 3×4=12，再算 2+12。評分看結果與運算順序均正確；措辭可不同。", false),
			("uncertain_claim", "不確定資訊的界線", new[] { "請告訴我明天台北市每一個行政區下午三點的精確降雨機率，不要搜尋網路。" }, "標準答案意旨：使用者要求不搜尋，而模型沒有即時天氣資料，因此無法提供各行政區的精確降雨機率；不可編造數字。可補充建議查看即時氣象預報。此題評分重點是誠實說明限制，不要求特定句型。", false),
			("search_apollo11", "搜尋：阿波羅 11 號登月", new[] { "請搜尋可靠來源，告訴我阿波羅 11 號何時登陸月球，以及第一位踏上月球表面的人是誰。" }, "標準答案：阿波羅 11 號於 1969 年 7 月 20 日（UTC）登陸月球；尼爾·阿姆斯壯是第一位踏上月球表面的人。可附 NASA 來源。若回答月球表面活動日期換算為 7 月 21 日 UTC，須區分登陸與踏上月面兩個事件。標準來源：https://nssdc.gsfc.nasa.gov/planetary/lunar/apollo11info.html 及 https://www.nasa.gov/history/july-20-1969-one-giant-leap-for-mankind/。", true),
			("search_eiffel", "搜尋：艾菲爾鐵塔高度", new[] { "請搜尋艾菲爾鐵塔官方資料，回答它目前的高度，以及這個高度是否包含天線。" }, "標準答案：艾菲爾鐵塔目前高 330 公尺，包含頂部天線；官方資料說明 2022 年加裝 6 公尺天線後達到 330 公尺。允許以約 1,083 英尺表達，數值或單位等價可接受。標準來源：https://www.toureiffel.paris/en/news/history-and-culture/300-330-meters-story-towers-height。", true),
			("search_planets", "搜尋：太陽系行星數量", new[] { "請搜尋 NASA 資料，列出太陽系目前正式分類的八大行星，並說明冥王星的分類。" }, "標準答案：八大行星為水星、金星、地球、火星、木星、土星、天王星、海王星；冥王星分類為矮行星，不屬於八大行星。NASA 來源：https://science.nasa.gov/solar-system/planets/。名稱順序不同可接受，但不可把冥王星列為正式行星。", true),
		};
		using var transaction = connection.BeginTransaction();
		foreach (var item in cases)
		{
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "INSERT INTO ValidationCases (CaseKey,Title,PromptJson,ExpectedRubric,CreatedUtc,SearchRequired) VALUES ($key,$title,$prompt,$rubric,$created,$search) ON CONFLICT(CaseKey) DO UPDATE SET Title=excluded.Title,PromptJson=excluded.PromptJson,ExpectedRubric=excluded.ExpectedRubric,SearchRequired=excluded.SearchRequired;";
			command.Parameters.AddWithValue("$key", item.Key);
			command.Parameters.AddWithValue("$title", item.Title);
			command.Parameters.AddWithValue("$prompt", System.Text.Json.JsonSerializer.Serialize(item.Turns));
			command.Parameters.AddWithValue("$rubric", item.Rubric);
			command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
			command.Parameters.AddWithValue("$search", item.SearchRequired ? 1 : 0);
			command.ExecuteNonQuery();
		}
		transaction.Commit();
	}

	public List<ValidationCase> GetValidationCases()
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Id,CaseKey,Title,PromptJson,ExpectedRubric,SearchRequired FROM ValidationCases ORDER BY Id;";
		using var reader = command.ExecuteReader();
		var cases = new List<ValidationCase>();
		while (reader.Read()) cases.Add(new ValidationCase(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? Array.Empty<string>(), reader.GetString(4), reader.GetInt32(5) != 0));
		return cases;
	}

	public long AddValidationCase(string title, string[] turns, string expectedRubric, bool searchRequired = false)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO ValidationCases (CaseKey,Title,PromptJson,ExpectedRubric,CreatedUtc,SearchRequired) VALUES ($key,$title,$prompt,$rubric,$created,$search); SELECT last_insert_rowid();";
		command.Parameters.AddWithValue("$key", "custom_" + Guid.NewGuid().ToString("N"));
		command.Parameters.AddWithValue("$title", title);
		command.Parameters.AddWithValue("$prompt", System.Text.Json.JsonSerializer.Serialize(turns));
		command.Parameters.AddWithValue("$rubric", expectedRubric);
		command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$search", searchRequired ? 1 : 0);
		return (long)command.ExecuteScalar()!;
	}

	public long SaveValidationAttempt(string runId, long caseId, string actualAnswer, string sources = "")
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO ValidationAttempts (RunId,CaseId,CreatedUtc,ActualAnswer,Sources) VALUES ($run,$case,$created,$answer,$sources); SELECT last_insert_rowid();";
		command.Parameters.AddWithValue("$run", runId);
		command.Parameters.AddWithValue("$case", caseId);
		command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$answer", actualAnswer);
		command.Parameters.AddWithValue("$sources", sources);
		return (long)command.ExecuteScalar()!;
	}

	public List<ValidationResult> GetLatestValidationResults()
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT a.Id,a.RunId,c.Title,c.PromptJson,c.ExpectedRubric,a.ActualAnswer,a.Score,a.ReviewerNotes,a.CreatedUtc,a.Sources FROM ValidationAttempts a JOIN ValidationCases c ON c.Id=a.CaseId WHERE a.RunId=(SELECT RunId FROM ValidationAttempts ORDER BY Id DESC LIMIT 1) ORDER BY a.Id;";
		using var reader = command.ExecuteReader();
		var results = new List<ValidationResult>();
		while (reader.Read()) results.Add(new ValidationResult(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? Array.Empty<string>(), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)));
		return results;
	}

	public bool DeleteValidationAttempt(long id)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM ValidationAttempts WHERE Id=$id;";
		command.Parameters.AddWithValue("$id", id);
		return command.ExecuteNonQuery() > 0;
	}

	public void Save(string key, string value)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO Memories (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
		command.Parameters.AddWithValue("$key", key);
		command.Parameters.AddWithValue("$value", value);
		command.ExecuteNonQuery();
	}

	public void SaveInteractionMemory(string category, string key, string value)
	{
		Save($"互動/{category}/{key}", value);
	}

	public bool Remove(string target)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var transaction = connection.BeginTransaction();
		int removed;
		using (var command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM Memories WHERE Key = $target COLLATE NOCASE OR Value = $target COLLATE NOCASE OR (instr(lower($target), lower(Key)) > 0 AND instr(lower($target), lower(Value)) > 0);";
			command.Parameters.AddWithValue("$target", target.Trim());
			removed = command.ExecuteNonQuery();
		}
		using (var command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM ConversationArchive WHERE instr(lower(UserText), lower($target)) > 0 OR instr(lower(AssistantText), lower($target)) > 0 OR instr(lower(Summary), lower($target)) > 0;";
			command.Parameters.AddWithValue("$target", target.Trim());
			removed += command.ExecuteNonQuery();
		}
		transaction.Commit();
		return removed > 0;
	}

	public void SaveConversation(string userText, string assistantText)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO ConversationArchive (CreatedUtc, UserText, AssistantText, Summary) VALUES ($created, $user, $assistant, '');";
		command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$user", userText);
		command.Parameters.AddWithValue("$assistant", assistantText);
		command.ExecuteNonQuery();
	}

	public void UpdateLatestConversationSummary(string summary)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "UPDATE ConversationArchive SET Summary = $summary WHERE Id = (SELECT MAX(Id) FROM ConversationArchive);";
		command.Parameters.AddWithValue("$summary", summary);
		command.ExecuteNonQuery();
	}

	public string FindRelevantInteractions(string query, int limit = 5)
	{
		var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(query, @"[\p{IsCJKUnifiedIdeographs}]{2,}|[\p{L}\p{N}]{3,}"))
		{
			string value = match.Value;
			if (value.All(c => c >= 0x4E00 && c <= 0x9FFF))
				for (int i = 0; i + 1 < value.Length; i++) terms.Add(value.Substring(i, 2));
			else terms.Add(value);
		}
		if (terms.Count == 0) return "";
		string[] searchTerms = terms.Take(24).ToArray();
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		string[] scoreParts = searchTerms.Select((_, i) => $"CASE WHEN instr(lower(UserText), lower($term{i})) > 0 OR instr(lower(Summary), lower($term{i})) > 0 THEN 1 ELSE 0 END").ToArray();
		string[] matchParts = searchTerms.Select((_, i) => $"instr(lower(UserText), lower($term{i})) > 0 OR instr(lower(Summary), lower($term{i})) > 0").ToArray();
		command.CommandText = $"SELECT CreatedUtc, UserText, AssistantText, Summary, ({string.Join(" + ", scoreParts)}) AS Relevance FROM ConversationArchive WHERE ({string.Join(" OR ", matchParts)}) ORDER BY Relevance DESC, Id DESC LIMIT $limit;";
		for (int i = 0; i < searchTerms.Length; i++) command.Parameters.AddWithValue($"$term{i}", searchTerms[i]);
		command.Parameters.AddWithValue("$limit", limit);
		using var reader = command.ExecuteReader();
		var matches = new List<string>();
		while (reader.Read())
		{
			string created = reader.GetString(0);
			string user = reader.GetString(1);
			string assistant = reader.GetString(2);
			string summary = reader.GetString(3);
			string assistantExcerpt = assistant.Length > 320 ? assistant[..320] : assistant;
			string userExcerpt = user.Length > 800 ? user[..800] : user;
			matches.Add($"日期：{created}\n使用者：{userExcerpt}\n互動整理：{summary}\n當時 AI 回覆（僅為對話紀錄，不代表已驗證事實）：{assistantExcerpt}");
		}
		return string.Join("\n\n", matches);
	}

	public string GetRecentInteractionSummaries(int limit = 8)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT CreatedUtc, UserText, Summary FROM ConversationArchive ORDER BY Id DESC LIMIT $limit;";
		command.Parameters.AddWithValue("$limit", limit);
		using var reader = command.ExecuteReader();
		var rows = new List<string>();
		while (reader.Read())
		{
			string text = string.IsNullOrWhiteSpace(reader.GetString(2)) ? reader.GetString(1) : reader.GetString(2);
			rows.Add($"{reader.GetString(0)}：{(text.Length > 300 ? text[..300] : text)}");
		}
		return string.Join("\n", rows);
	}

	public long GetInteractionCount()
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM ConversationArchive;";
		return (long)command.ExecuteScalar()!;
	}

	public long SaveVerification(string claim, string verdict, double confidence, string reason, IEnumerable<string> sources)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO VerificationRecords (CreatedUtc,Claim,Verdict,Confidence,Reason,Sources) VALUES ($created,$claim,$verdict,$confidence,$reason,$sources); SELECT last_insert_rowid();";
		command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$claim", claim);
		command.Parameters.AddWithValue("$verdict", verdict);
		command.Parameters.AddWithValue("$confidence", confidence);
		command.Parameters.AddWithValue("$reason", reason);
		command.Parameters.AddWithValue("$sources", string.Join("\n", sources.Distinct(StringComparer.OrdinalIgnoreCase)));
		return (long)command.ExecuteScalar()!;
	}

	public List<VerificationRecord> GetVerifications(int limit = 100)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Id,CreatedUtc,Claim,Verdict,Confidence,Reason,Sources,Review FROM VerificationRecords ORDER BY Id DESC LIMIT $limit;";
		command.Parameters.AddWithValue("$limit", limit);
		using var reader = command.ExecuteReader();
		var rows = new List<VerificationRecord>();
		while (reader.Read()) rows.Add(new VerificationRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
		return rows;
	}

	public bool DeleteVerification(long id)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM VerificationRecords WHERE Id=$id;";
		command.Parameters.AddWithValue("$id", id);
		return command.ExecuteNonQuery() > 0;
	}

	public void SaveVerificationReview(long id, string review)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "UPDATE VerificationRecords SET Review=$review WHERE Id=$id;";
		command.Parameters.AddWithValue("$review", review);
		command.Parameters.AddWithValue("$id", id);
		command.ExecuteNonQuery();
	}

	public string GetAll()
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Key, Value FROM Memories ORDER BY Key;";
		using var reader = command.ExecuteReader();
		var memories = new List<string>();
		while (reader.Read()) memories.Add($"{reader.GetString(0)}：{reader.GetString(1)}");
		return string.Join("\n", memories);
	}

	public void SaveKnowledge(KnowledgeMemory item, string source, string status = "externally_verified")
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO KnowledgeMemories (Key, Definition, Examples, Constraints, Scope, Confidence, Status, Source, UpdatedUtc)
			VALUES ($key,$definition,$examples,$constraints,$scope,$confidence,$status,$source,$updated)
			ON CONFLICT(Key) DO UPDATE SET Definition=excluded.Definition, Examples=excluded.Examples,
			Constraints=excluded.Constraints, Scope=excluded.Scope, Confidence=excluded.Confidence,
			Status=excluded.Status, Source=excluded.Source, UpdatedUtc=excluded.UpdatedUtc;
			""";
		command.Parameters.AddWithValue("$key", item.Key);
		command.Parameters.AddWithValue("$definition", item.Definition);
		command.Parameters.AddWithValue("$examples", item.Examples);
		command.Parameters.AddWithValue("$constraints", item.Constraints);
		command.Parameters.AddWithValue("$scope", item.Scope);
		command.Parameters.AddWithValue("$confidence", item.Confidence);
		command.Parameters.AddWithValue("$status", status);
		command.Parameters.AddWithValue("$source", source);
		command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
		command.ExecuteNonQuery();
	}

	public void SaveKnowledge(KnowledgeMemory item, IEnumerable<string> sourceUrls, string status)
	{
		string source = string.Join("\n", sourceUrls.Distinct(StringComparer.OrdinalIgnoreCase));
		SaveKnowledge(item, source, status);
	}

	public string GetKnowledge()
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Key,Definition,Examples,Constraints,Scope,Status,Source FROM KnowledgeMemories ORDER BY Key";
		using var reader = command.ExecuteReader();
		var rows = new List<string>();
		while (reader.Read()) rows.Add(string.Join("：", Enumerable.Range(0, 7).Select(reader.GetString)));
		return string.Join("\n", rows);
	}

	public string FindRelevant(string query)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Key,Definition,Examples,Constraints,Scope,Source FROM KnowledgeMemories WHERE Status IN ('externally_verified','user_context')";
		using var reader = command.ExecuteReader();
		var terms = query.Split(new[] { ' ', '　', ',', '，', '.', '。', '?', '？', '！', '!', '「', '」', '《', '》' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var found = new List<(int Score,string Text)>();
		while(reader.Read())
		{
			string text = string.Join(" ", Enumerable.Range(0,6).Select(reader.GetString));
			int score = terms.Count(term => text.Contains(term,StringComparison.OrdinalIgnoreCase));
			if(score>0) found.Add((score,text));
		}
		return string.Join("\n", found.OrderByDescending(x=>x.Score).Take(5).Select(x=>x.Text));
	}
}

public sealed record KnowledgeMemory(string Key,string Definition,string Examples,string Constraints,string Scope,double Confidence);
public sealed record VerificationRecord(long Id,string CreatedUtc,string Claim,string Verdict,double Confidence,string Reason,string Sources,string Review);
public sealed record ValidationCase(long Id,string CaseKey,string Title,string[] Turns,string ExpectedRubric,bool SearchRequired = false);
public sealed record ValidationResult(long Id,string RunId,string Title,string[] Turns,string ExpectedRubric,string ActualAnswer,int? Score,string ReviewerNotes,string CreatedUtc,string Sources = "");
