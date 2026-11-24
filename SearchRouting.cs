internal static class SearchRouting
{
    public static bool IsExplicitNoSearchRequest(string input) =>
        new[] { "先不要搜尋", "先不要搜索", "先不搜尋", "先不搜索", "不要搜尋網路", "不要搜索網路", "不要搜尋網絡", "不要上網搜尋", "不要上網搜索", "不用搜尋網路", "不用搜索網路", "不必搜尋網路", "不必搜索網路", "不要搜尋", "不要搜索", "不用上網查", "不查網路", "不查網絡", "不要查網路", "不要查網絡", "不用查資料" }
            .Any(phrase => input.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    public static bool IsMemoryStatusQuestion(string input) =>
        System.Text.RegularExpressions.Regex.IsMatch(input,
            @"你.{0,30}(記住|記得|記下|記憶|存下|存好|儲存|保存).{0,30}(嗎|嗎？|沒|沒有|了嗎|了沒|？|\?)|你(有沒有|是否|有將|有把|已經).{0,40}(記住|記得|記下|儲存|保存)|^(還記得|記住了嗎|有記住|有沒有記住)") &&
        !new[] { "搜尋", "查詢", "再查", "重新查", "查一下", "查一次", "google", "/search", "/google" }
            .Any(x => input.Contains(x, StringComparison.OrdinalIgnoreCase));

    public static bool IsRefinement(string input) => !IsMemoryStatusQuestion(input) && new[]
    {
        "再查", "在查", "重新查", "查一次", "就好", "我不是說", "全部", "完整名單", "怎麼問一半"
    }.Any(x => input.Contains(x, StringComparison.OrdinalIgnoreCase));

    public static bool RequiresSearch(string input, string? previousIntent) =>
        !IsMemoryStatusQuestion(input) && (new[] { "查詢", "搜尋", "查一下", "查一次", "再查", "在查", "重新查" }
            .Any(x => input.Contains(x, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(previousIntent) && IsRefinement(input)));

    public static string CompleteIntent(string input, string plannedIntent, string? previousIntent)
    {
        if (IsMemoryStatusQuestion(input)) return input;
        // Keep user constraints even when the planner returns only the latest fragment.
        if (IsRefinement(input) && !string.IsNullOrWhiteSpace(previousIntent))
            return $"{previousIntent}\n使用者補充要求：{input}";
        return string.IsNullOrWhiteSpace(plannedIntent) ? input : plannedIntent;
    }
}
