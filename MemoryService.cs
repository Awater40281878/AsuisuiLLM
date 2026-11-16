
using Microsoft.Data.Sqlite;

public class MemoryService
{
	private readonly string connectionString;

	public MemoryService()
	{
		string dbPath = Path.Combine(
			AppContext.BaseDirectory, "memory.db");

		connectionString = $"Data Source={dbPath}";

		using var connection = new SqliteConnection(connectionString);
		connection.Open();

		using var command = connection.CreateCommand();
		command.CommandText = """
            CREATE TABLE IF NOT EXISTS Memories (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
		command.ExecuteNonQuery();
	}

	public void Save(string key, string value)
	{
		using var connection = new SqliteConnection(connectionString);
		connection.Open();

		using var command = connection.CreateCommand();
		command.CommandText = """
            INSERT INTO Memories (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key)
            DO UPDATE SET Value = excluded.Value;
            """;

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

		while (reader.Read())
			memories.Add($"{reader.GetString(0)}¡G{reader.GetString(1)}");

		return string.Join("\n", memories);
	}
}
