using System.Text;
using System.Text.Json;

public sealed class GoogleProfileSettings
{
	private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsuisuiLLM", "google-profile.txt");
	public string SelectedPath
	{
		get => File.Exists(settingsPath) ? File.ReadAllText(settingsPath, Encoding.UTF8).Trim() : "";
		set { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!); File.WriteAllText(settingsPath, value, Encoding.UTF8); }
	}
}

public sealed record ChromeProfileOption(string Name, string Path);

public static class ChromeProfileSettings
{
	public static List<ChromeProfileOption> GetProfiles()
	{
		string[] roots =
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Google", "Chrome", "User Data")
		};
		string? root = roots.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "Local State")));
		if (root is null) return new();
		using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Local State"), Encoding.UTF8));
		if (!json.RootElement.TryGetProperty("profile", out var profile) || !profile.TryGetProperty("info_cache", out var info)) return new();
		var result = new List<ChromeProfileOption>();
		foreach (var item in info.EnumerateObject())
		{
			string name = item.Value.TryGetProperty("name", out var n) ? n.GetString() ?? item.Name : item.Name;
			string path = Path.Combine(root, item.Name);
			if (Directory.Exists(path)) result.Add(new ChromeProfileOption(name, path));
		}
		return result;
	}
}
