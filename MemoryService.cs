using Microsoft.Data.Sqlite;

public class MemoryService
{
	private readonly string connectionString;

	public MemoryService()
	{
		string dbPath = Path.Combine(AppContext.BaseDirectory, "memory.db");
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
			""";
		command.ExecuteNonQuery();
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

	public string GetAll()
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT Key, Value FROM Memories;";
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