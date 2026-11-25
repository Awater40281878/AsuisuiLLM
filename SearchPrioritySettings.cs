using System.Text;

public enum SearchDiscoveryProvider { Web, GoogleAi, ChatGpt }

public sealed class SearchPrioritySettings
{
	private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsuisuiLLM", "search-priority.txt");
	public IReadOnlyList<SearchDiscoveryProvider> Get()
	{
		string saved = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "ChatGpt,GoogleAi,Web";
		var providers = saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(value => Enum.TryParse<SearchDiscoveryProvider>(value, true, out var provider) ? provider : (SearchDiscoveryProvider?)null)
			.Where(provider => provider.HasValue).Select(provider => provider!.Value).Distinct().ToList();
		return providers.Count == 0 ? new[] { SearchDiscoveryProvider.Web, SearchDiscoveryProvider.GoogleAi, SearchDiscoveryProvider.ChatGpt } : providers;
	}
	public void Set(string order)
	{
		var providers = order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(value => Enum.TryParse<SearchDiscoveryProvider>(value, true, out var provider) ? provider : (SearchDiscoveryProvider?)null)
			.Where(provider => provider.HasValue).Select(provider => provider!.Value).Distinct().ToList();
		if (providers.Count == 0) throw new ArgumentException("請設定至少一個來源：Web、GoogleAi、ChatGpt。", nameof(order));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, string.Join(',', providers), Encoding.UTF8);
	}
}
