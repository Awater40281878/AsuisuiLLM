using System.Text.Json.Serialization;

namespace DesktopAI.Core.ComputerUse;

public enum ComputerActionKind
{
	Observe,
	Click,
	TypeText,
	PressKey,
	Scroll
}

public sealed record ComputerUseSettings
{
	public string PlannerModel { get; init; } = "qwen3:4b-instruct";
	public string VisionModel { get; init; } = "qwen3-vl:8b-instruct";
	public bool EnableComputerUse { get; init; }
	public bool RequireConfirmationForInput { get; init; } = true;
	public bool RequireConfirmationForSensitiveActions { get; init; } = true;
}

public sealed record ComputerActionRequest(
	ComputerActionKind Kind,
	string? TargetName = null,
	string? Text = null,
	string? Key = null,
	int? X = null,
	int? Y = null,
	int? ScrollDelta = null);

public sealed record ComputerActionDecision(bool Allowed, bool RequiresConfirmation, string Reason)
{
	public static ComputerActionDecision Deny(string reason) => new(false, false, reason);
	public static ComputerActionDecision Allow(string reason) => new(true, false, reason);
	public static ComputerActionDecision Confirm(string reason) => new(false, true, reason);
}

/// <summary>Pure policy gate. It decides whether an action may be offered to an executor.</summary>
public sealed class ComputerUsePolicy(ComputerUseSettings settings)
{
	private static readonly string[] SensitiveTerms = ["delete", "remove", "format", "submit", "send", "purchase", "pay", "刪除", "移除", "格式化", "送出", "傳送", "購買", "付款", "覆寫"];

	public ComputerActionDecision Evaluate(ComputerActionRequest request)
	{
		if (!settings.EnableComputerUse)
			return ComputerActionDecision.Deny("電腦操作功能尚未啟用。");

		if (request.Kind == ComputerActionKind.Observe)
			return ComputerActionDecision.Allow("允許讀取目前視窗狀態。");

		if (request.Kind == ComputerActionKind.TypeText && string.IsNullOrWhiteSpace(request.Text))
			return ComputerActionDecision.Deny("輸入內容不可為空。");

		if (request.Kind is ComputerActionKind.Click or ComputerActionKind.Scroll && (request.X is null || request.Y is null))
			return ComputerActionDecision.Deny("座標操作必須提供目標位置。");

		string actionText = string.Join(' ', request.TargetName, request.Text, request.Key);
		if (settings.RequireConfirmationForSensitiveActions && SensitiveTerms.Any(term => actionText.Contains(term, StringComparison.OrdinalIgnoreCase)))
			return ComputerActionDecision.Confirm("此操作可能送出、刪除或變更資料，必須先取得使用者確認。");

		if (settings.RequireConfirmationForInput && request.Kind is ComputerActionKind.Click or ComputerActionKind.TypeText or ComputerActionKind.PressKey or ComputerActionKind.Scroll)
			return ComputerActionDecision.Confirm("即將對電腦執行輸入操作，必須先取得使用者確認。");

		return ComputerActionDecision.Allow("操作符合目前安全政策。");
	}
}

public sealed record ComputerWindowInfo(nint Handle, string Title, string ProcessName, bool IsForeground);

public interface IComputerUseObserver
{
	Task<IReadOnlyList<ComputerWindowInfo>> GetWindowsAsync(CancellationToken cancellationToken = default);
	Task<byte[]> CaptureScreenAsync(string? displayId = null, CancellationToken cancellationToken = default);
}

public interface IComputerUseExecutor
{
	Task ExecuteAsync(ComputerActionRequest request, CancellationToken cancellationToken = default);
}
