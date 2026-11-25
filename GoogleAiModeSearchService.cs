using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

public sealed class GoogleAiModeSearchService : IDisposable
{
	private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
	private const string GoogleAiModeUrl = "https://www.google.com/ai";
	private const string ChatGptUrl = "https://chatgpt.com/";
	private const int BridgePort = 43829;
	private const string BridgeOrigin = "chrome-extension://lcocmombemlmdnaflofijmaohefbhfma";
	private const string BridgeToken = "asuisui-ggo-bridge-v1-7f2f3fd64fcb45a1b313d33d0eccaf27";
	private readonly object sync = new();
	private readonly SemaphoreSlim searchJobLock = new(1, 1);
	private readonly TcpListener bridge = new(IPAddress.Loopback, BridgePort);
	private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
	private Task? bridgeTask;
	private BridgeJob? currentJob;
	private bool pollLogged;
	private TaskCompletionSource<bool>? extensionReloadRequested;
	private TaskCompletionSource<bool>? extensionReloadReady;
	private TaskCompletionSource<bool>? submitted;
	private TaskCompletionSource<GoogleAiModeResult>? completed;
	private string chromeProfileDirectory = "Default";
	private string chromeUserDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");

	public void SetProfilePath(string path)
	{
		string fullPath = Path.GetFullPath(path);
		chromeProfileDirectory = Path.GetFileName(fullPath);
		chromeUserDataDirectory = Directory.GetParent(fullPath)?.FullName
			?? throw new ArgumentException("Chrome 設定檔路徑無法識別其 User Data 目錄。", nameof(path));
		if (string.IsNullOrWhiteSpace(chromeProfileDirectory))
			throw new ArgumentException("Chrome 設定檔名稱無效。", nameof(path));
	}

	public Task<GoogleAiModeResult> SearchAsync(string query, CancellationToken cancellationToken = default) =>
		SearchAsync(query, "google", cancellationToken, null);

	public Task<GoogleAiModeResult> SearchChatGptAsync(string query, CancellationToken cancellationToken = default) =>
		SearchAsync(query, "chatgpt", cancellationToken, null);

	public Task<GoogleAiModeResult> SearchImageAsync(string query, byte[] png, CancellationToken cancellationToken = default)
	{
		if (png.Length is 0 or > 12_000_000) throw new ArgumentException("圖片大小不符合限制。");
		return SearchAsync(query, "google", cancellationToken, Convert.ToBase64String(png));
	}

	public async Task SendChatGptQuestionWithUiVisionAsync(string query, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("問題不可為空。", nameof(query));
		cancellationToken.ThrowIfCancellationRequested();

		string scriptPath = Path.Combine(AppContext.BaseDirectory, "Invoke-UiVisionChatGPT.ps1");
		string launcherPath = Path.Combine(AppContext.BaseDirectory, "RPA", "ui.vision.html");
		string powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
		if (!File.Exists(scriptPath)) throw new FileNotFoundException("找不到 UI.Vision PowerShell 啟動腳本。", scriptPath);
		if (!File.Exists(launcherPath)) throw new FileNotFoundException("找不到 UI.Vision Autostart HTML。", launcherPath);
		if (!File.Exists(powershellPath)) throw new FileNotFoundException("找不到 Windows PowerShell。", powershellPath);

		var startInfo = new ProcessStartInfo
		{
			FileName = powershellPath,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("-NoProfile");
		startInfo.ArgumentList.Add("-ExecutionPolicy");
		startInfo.ArgumentList.Add("Bypass");
		startInfo.ArgumentList.Add("-File");
		startInfo.ArgumentList.Add(scriptPath);
		startInfo.ArgumentList.Add("-Question");
		startInfo.ArgumentList.Add(query);
		startInfo.ArgumentList.Add("-MacroName");
		startInfo.ArgumentList.Add("SendChatGPT.js");
		startInfo.ArgumentList.Add("-UserDataDirectory");
		startInfo.ArgumentList.Add(chromeUserDataDirectory);
		startInfo.ArgumentList.Add("-ProfileName");
		startInfo.ArgumentList.Add(chromeProfileDirectory);

		HashSet<IntPtr> windowsBeforeLaunch = GetChromeWindowHandles();
		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("無法啟動 UI.Vision PowerShell 流程。");
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync(cancellationToken);
		string stdout = await stdoutTask;
		string stderr = await stderrTask;
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"UI.Vision 啟動失敗：{stderr.Trim()} {stdout.Trim()}".Trim());

		IntPtr chromeWindow = await WaitForNewGoogleChromeWindowAsync(windowsBeforeLaunch, "chatgpt", query, cancellationToken);
		ShowWindow(chromeWindow, 4); // Keep the GGO window visible without taking focus.
		if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
		Console.WriteLine("GGO 視窗已開啟，Ui.Vision 巨集已啟動；請以 Ui.Vision 執行記錄確認送出結果。本階段不擷取 ChatGPT 回答。");
	}

	private async Task<GoogleAiModeResult> SearchAsync(string query, string provider, CancellationToken cancellationToken, string? imageBase64)
	{
		// The bridge has one active job slot. Serialize provider jobs so a second
		// request cannot replace a ChatGPT job or trigger window cleanup mid-answer.
		await searchJobLock.WaitAsync(cancellationToken);
		try { return await SearchCoreAsync(query, provider, cancellationToken, imageBase64); }
		finally { searchJobLock.Release(); }
	}

	private async Task<GoogleAiModeResult> SearchCoreAsync(string query, string provider, CancellationToken cancellationToken, string? imageBase64)
	{
		if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("搜尋問題不可為空。", nameof(query));
		EnsureBridgeStarted();
		cancellationToken.ThrowIfCancellationRequested();

		string jobId = Guid.NewGuid().ToString("N");
		var job = new BridgeJob(jobId, query, provider, ReadExtensionVersion(), imageBase64);
		var reloadSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var reloadReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var submittedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var resultSignal = new TaskCompletionSource<GoogleAiModeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (sync)
		{
			currentJob = job;
			pollLogged = false;
			extensionReloadRequested = reloadSignal;
			extensionReloadReady = reloadReadySignal;
			submitted = submittedSignal;
			completed = resultSignal;
		}

		IntPtr chromeWindow = IntPtr.Zero;
		try
		{
			HashSet<IntPtr> windowsBeforeLaunch = GetChromeWindowHandles();
			using var process = Process.Start(CreateChromeStartInfo(job.Id, provider));
			if (process is null) throw new InvalidOperationException("無法啟動 Chrome。");

			Task firstSignal;
			try
			{
				firstSignal = await Task.WhenAny(submittedSignal.Task, resultSignal.Task, reloadSignal.Task)
					.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
			}
			catch (TimeoutException)
			{
				throw new TimeoutException($"沒有收到 {GetProviderLabel(provider)} 擴充功能的送出通知。請確認指定 Chrome 設定檔已載入最新 Asuisui AI Web Bridge 擴充功能。");
			}
			chromeWindow = FindNewGoogleChromeWindow(windowsBeforeLaunch, provider, query);
			if (chromeWindow != IntPtr.Zero) ShowWindow(chromeWindow, 4); // SW_SHOWNOACTIVATE

			if (firstSignal == reloadSignal.Task)
			{
				Console.WriteLine($"偵測到 GGO 擴充功能版本落後（目前 {job.ExtensionVersion}）；正在背景重新載入並重開本次搜尋……");
				if (chromeWindow != IntPtr.Zero) ShowWindow(chromeWindow, 4);
				try { await reloadReadySignal.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
				catch (TimeoutException)
				{
					throw new TimeoutException("GGO 擴充功能未回報重新載入完成；請確認載入版本包含自動重載功能。");
				}
				var nextReloadSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				lock (sync) extensionReloadRequested = nextReloadSignal;
				reloadSignal = nextReloadSignal;

				HashSet<IntPtr> windowsBeforeRetry = GetChromeWindowHandles();
				using var retryProcess = Process.Start(CreateChromeStartInfo(job.Id, provider));
				if (retryProcess is null) throw new InvalidOperationException("重新載入擴充功能後無法重開 Chrome 搜尋視窗。");

				Task retrySignal;
				try
				{
					retrySignal = await Task.WhenAny(submittedSignal.Task, resultSignal.Task, reloadSignal.Task)
						.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
				}
				catch (TimeoutException)
				{
					throw new TimeoutException("GGO 擴充功能自動重新載入後仍未完成搜尋啟動；搜尋頁面會保留以便檢查。");
				}
				chromeWindow = FindNewGoogleChromeWindow(windowsBeforeRetry, provider, query);
				if (chromeWindow != IntPtr.Zero) ShowWindow(chromeWindow, 4);
				if (retrySignal == reloadSignal.Task)
					throw new InvalidOperationException("GGO 擴充功能自動重新載入後版本仍落後；請檢查擴充功能路徑與載入設定。");
				if (retrySignal == resultSignal.Task)
					await resultSignal.Task;
			}
			else if (firstSignal == resultSignal.Task)
			{
				await resultSignal.Task;
			}

			// The bridge's submission signal is authoritative. Window discovery is only
			// for the optional no-activate display hint; it must not fail a real search.
			if (chromeWindow == IntPtr.Zero)
				chromeWindow = FindNewGoogleChromeWindow(windowsBeforeLaunch, provider, query);
			if (chromeWindow != IntPtr.Zero) ShowWindow(chromeWindow, 4);
			Console.WriteLine(chromeWindow == IntPtr.Zero
				? $"{GetProviderLabel(provider)} 擴充功能已確認送出；目前無法取得視窗控制代碼，仍繼續等待回答。"
				: $"本次 {GetProviderLabel(provider)} Chrome 視窗保持顯示（不搶焦點、不最小化），等待回答……");

			// Response generation has no fixed deadline. In particular, do not release
			// the shared bridge slot while ChatGPT is still generating: the next search
			// would replace this job and could close its tab. Cancellation is explicit.
			GoogleAiModeResult result = await resultSignal.Task.WaitAsync(cancellationToken);

			Console.WriteLine($"已讀取 {GetProviderLabel(provider)} 網頁內容；擴充功能只會關閉本次搜尋分頁。");
			return result;
		}
		finally
		{
			lock (sync)
			{
				currentJob = null;
				extensionReloadRequested = null;
				extensionReloadReady = null;
				submitted = null;
				completed = null;
			}
		}
	}

	private void EnsureBridgeStarted()
	{
		if (bridgeTask is not null) return;
		bridge.Start();
		bridgeTask = Task.Run(BridgeLoopAsync);
	}

	private async Task BridgeLoopAsync()
	{
		while (bridge.Server.IsBound)
		{
			TcpClient client;
			try { client = await bridge.AcceptTcpClientAsync(); }
			catch (ObjectDisposedException) { return; }
			catch (SocketException) { return; }
			_ = Task.Run(() => HandleBridgeRequestAsync(client));
		}
	}

	private async Task HandleBridgeRequestAsync(TcpClient client)
	{
		using (client)
		{
			try
			{
				NetworkStream stream = client.GetStream();
				(string method, string path, int contentLength, string origin, string token, string requestedJobId) = await ReadRequestHeadersAsync(stream);
				if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, BridgeOrigin, StringComparison.Ordinal))
				{
					await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden origin\"}", "application/json; charset=utf-8");
					return;
				}
				if (method == "GET" && path == "/health")
				{
					if (!string.Equals(token, BridgeToken, StringComparison.Ordinal))
					{
						await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden token\"}", "application/json; charset=utf-8");
						return;
					}
					await WriteResponseAsync(stream, 200, "{\"ok\":true}", "application/json; charset=utf-8");
					return;
				}

				if (method == "GET" && path == "/job")
				{
					if (!string.Equals(token, BridgeToken, StringComparison.Ordinal))
					{
						Console.WriteLine($"{GetProviderLabel(currentJob?.Provider ?? "google")} 本機橋接拒絕 /job：驗證標記不符。");
						await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden token\"}", "application/json; charset=utf-8");
						return;
					}
					BridgeJob? job;
					lock (sync)
					{
						job = currentJob is not null && currentJob.Id == requestedJobId && submitted?.Task.IsCompleted != true ? currentJob : null;
						if (job is not null && !pollLogged)
						{
							pollLogged = true;
							Console.WriteLine($"{GetProviderLabel(job.Provider)} 擴充功能已連上本機橋接，正在傳送搜尋工作。");
						}
					}
					await WriteResponseAsync(stream, job is null ? 204 : 200,
						job is null ? "" : JsonSerializer.Serialize(job, jsonOptions), "application/json; charset=utf-8");
					return;
				}

				string body = await ReadRequestBodyAsync(stream, contentLength);
				if (method == "POST" && path == "/reload")
				{
					if (!string.Equals(token, BridgeToken, StringComparison.Ordinal))
					{
						await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden token\"}", "application/json; charset=utf-8");
						return;
					}
					var request = JsonSerializer.Deserialize<BridgeReloadRequest>(body, jsonOptions);
					bool accepted = false;
					lock (sync)
					{
						if (request is not null && currentJob is { } activeJob && request.JobId == activeJob.Id &&
							request.ExpectedVersion == activeJob.ExtensionVersion && IsExtensionVersionOlder(request.InstalledVersion, activeJob.ExtensionVersion))
						{
							accepted = extensionReloadRequested?.TrySetResult(true) == true;
							if (accepted)
								Console.WriteLine($"GGO 擴充功能版本 {request.InstalledVersion} 落後於 {activeJob.ExtensionVersion}；已要求擴充功能自行重新載入。");
						}
					}
					await WriteResponseAsync(stream, accepted ? 200 : 409,
						accepted ? "{\"ok\":true}" : "{\"error\":\"reload not required or job mismatch\"}",
						"application/json; charset=utf-8");
					return;
				}

				if (method == "POST" && path == "/reload-ready")
				{
					if (!string.Equals(token, BridgeToken, StringComparison.Ordinal))
					{
						await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden token\"}", "application/json; charset=utf-8");
						return;
					}
					var ready = JsonSerializer.Deserialize<BridgeReloadReady>(body, jsonOptions);
					bool accepted = false;
					lock (sync)
					{
						if (ready is not null && currentJob is { } activeJob && ready.JobId == activeJob.Id &&
							ready.ExtensionVersion == activeJob.ExtensionVersion)
						{
							accepted = extensionReloadReady?.TrySetResult(true) == true;
							if (accepted) Console.WriteLine($"GGO 擴充功能已重新載入至版本 {activeJob.ExtensionVersion}。");
						}
					}
					await WriteResponseAsync(stream, accepted ? 200 : 409,
						accepted ? "{\"ok\":true}" : "{\"error\":\"reload not expected or version mismatch\"}",
						"application/json; charset=utf-8");
					return;
				}

				if (method == "POST" && path == "/diagnostic")
				{
					if (!string.Equals(token, BridgeToken, StringComparison.Ordinal))
					{
						await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden token\"}", "application/json; charset=utf-8");
						return;
					}
					using var diagnostic = JsonDocument.Parse(body);
					string? diagnosticJobId = diagnostic.RootElement.TryGetProperty("jobId", out var jobIdElement) ? jobIdElement.GetString() : null;
					string? stage = diagnostic.RootElement.TryGetProperty("stage", out var stageElement) ? stageElement.GetString() : null;
					lock (sync)
						if (diagnosticJobId == currentJob?.Id && !string.IsNullOrWhiteSpace(stage) && currentJob is { } activeJob)
							Console.WriteLine($"{GetProviderLabel(activeJob.Provider)} 擴充功能診斷：{stage}");
					await WriteResponseAsync(stream, 200, "{\"ok\":true}", "application/json; charset=utf-8");
					return;
				}

				if (method == "POST" && path == "/submitted")
				{
					if (!string.Equals(token, BridgeToken, StringComparison.Ordinal))
					{
						await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden token\"}", "application/json; charset=utf-8");
						return;
					}
					var message = JsonSerializer.Deserialize<BridgeStatus>(body, jsonOptions);
					lock (sync)
						if (message?.JobId == currentJob?.Id) submitted?.TrySetResult(true);
					await WriteResponseAsync(stream, 200, "{\"ok\":true}", "application/json; charset=utf-8");
					return;
				}

				if (method == "POST" && path == "/result")
				{
					if (!string.Equals(token, BridgeToken, StringComparison.Ordinal))
					{
						await WriteResponseAsync(stream, 403, "{\"error\":\"forbidden token\"}", "application/json; charset=utf-8");
						return;
					}
					var message = JsonSerializer.Deserialize<BridgeResult>(body, jsonOptions);
				bool accepted = false;
					lock (sync)
					{
						if (message is not null && message.JobId == currentJob?.Id && completed is not null)
						{
							if (!string.IsNullOrWhiteSpace(message.Error))
							accepted = completed.TrySetException(new InvalidOperationException(message.Error));
							else
							{
								List<GoogleAiSource> sources = message.Sources ?? new();
								string provider = currentJob?.Provider ?? "google";
								string providerLabel = GetProviderLabel(provider);
								string evidence = $"【{providerLabel} 原始回答】\n{message.Answer}";
								if (sources.Count > 0) evidence += $"\n\n【{providerLabel} 頁面引用】\n" + string.Join("\n", sources.Select(s => $"{s.Title}\n{s.Url}"));
							accepted = completed.TrySetResult(new GoogleAiModeResult(evidence, sources, message.Answer ?? ""));
							}
						}
					}
				await WriteResponseAsync(stream, accepted ? 200 : 404,
					accepted ? "{\"ok\":true}" : "{\"error\":\"job not active or result already received\"}",
					"application/json; charset=utf-8");
					return;
				}

				await WriteResponseAsync(stream, 404, "{\"error\":\"not found\"}", "application/json; charset=utf-8");
			}
			catch
			{
				try { await WriteResponseAsync(client.GetStream(), 500, "{\"error\":\"bridge error\"}", "application/json; charset=utf-8"); }
				catch { }
			}
		}
	}

	private static Task<(string Method, string Path, int ContentLength, string Origin, string Token, string RequestedJobId)> ReadRequestHeadersAsync(NetworkStream stream)
	{
		var bytes = new List<byte>(1024);
		byte[] end = { 13, 10, 13, 10 };
		while (bytes.Count < 16384)
		{
			int next = stream.ReadByte();
			if (next < 0) throw new IOException("連線在 HTTP 標頭完成前中斷。");
			bytes.Add((byte)next);
			int count = bytes.Count;
			if (count >= 4 && bytes[count - 4] == end[0] && bytes[count - 3] == end[1] && bytes[count - 2] == end[2] && bytes[count - 1] == end[3]) break;
		}
		string headers = Encoding.ASCII.GetString(bytes.ToArray());
		string[] lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		string[] request = lines[0].Split(' ', 3);
		int contentLength = 0;
		string origin = "";
		string requestTarget = request[1];
		string token = requestTarget.Split('?', 2).Skip(1).FirstOrDefault()?
			.Split('&', StringSplitOptions.RemoveEmptyEntries)
			.Select(part => part.Split('=', 2))
			.Where(pair => pair.Length == 2 && pair[0] == "token")
			.Select(pair => Uri.UnescapeDataString(pair[1]))
			.FirstOrDefault() ?? "";
		string requestedJobId = requestTarget.Split('?', 2).Skip(1).FirstOrDefault()?
			.Split('&', StringSplitOptions.RemoveEmptyEntries)
			.Select(part => part.Split('=', 2))
			.Where(pair => pair.Length == 2 && pair[0] == "jobId")
			.Select(pair => Uri.UnescapeDataString(pair[1]))
			.FirstOrDefault() ?? "";
		string path = requestTarget.Split('?', 2)[0];
		foreach (string line in lines.Skip(1))
		{
			if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
				int.TryParse(line[15..].Trim(), out contentLength);
			if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = line[7..].Trim();
		}
		return Task.FromResult((request[0], path, contentLength, origin, token, requestedJobId));
	}

	private static async Task<string> ReadRequestBodyAsync(NetworkStream stream, int length)
	{
		if (length <= 0) return "";
		if (length > 2_000_000) throw new InvalidDataException("Bridge request is too large.");
		byte[] body = new byte[length];
		int read = 0;
		while (read < length)
		{
			int count = await stream.ReadAsync(body.AsMemory(read, length - read));
			if (count == 0) throw new IOException("HTTP request body ended early.");
			read += count;
		}
		return Encoding.UTF8.GetString(body);
	}

	private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, string contentType)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(body);
		string reason = status switch { 200 => "OK", 204 => "No Content", 404 => "Not Found", _ => "Internal Server Error" };
		string headers = $"HTTP/1.1 {status} {reason}\r\n" +
			$"Content-Type: {contentType}\r\n" +
			$"Content-Length: {bytes.Length}\r\n" +
			$"Access-Control-Allow-Origin: {BridgeOrigin}\r\n" +
			"Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
			"Access-Control-Allow-Headers: Content-Type\r\n" +
			"Access-Control-Allow-Private-Network: true\r\n" +
			"Cache-Control: no-store\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
		if (bytes.Length > 0) await stream.WriteAsync(bytes);
	}

	private ProcessStartInfo CreateChromeStartInfo(string jobId, string provider)
	{
		if (!File.Exists(ChromePath)) throw new FileNotFoundException("找不到 Chrome。請確認安裝路徑。", ChromePath);
		var startInfo = new ProcessStartInfo
		{
			FileName = ChromePath,
			UseShellExecute = true,
			WindowStyle = ProcessWindowStyle.Normal
		};
		startInfo.ArgumentList.Add($"--user-data-dir={chromeUserDataDirectory}");
		startInfo.ArgumentList.Add($"--profile-directory={chromeProfileDirectory}");
		startInfo.ArgumentList.Add("--new-window");
		string startUrl = provider == "chatgpt" ? ChatGptUrl : GoogleAiModeUrl;
		startInfo.ArgumentList.Add($"{startUrl}#asuisui-job={Uri.EscapeDataString(jobId)}&provider={provider}");
		return startInfo;
	}

	private static async Task<IntPtr> WaitForNewGoogleChromeWindowAsync(HashSet<IntPtr> existingWindows, string provider, string query, CancellationToken cancellationToken)
	{
		for (int i = 0; i < 120; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IntPtr target = FindNewGoogleChromeWindow(existingWindows, provider, query);
			if (target != IntPtr.Zero) return target;
			await Task.Delay(250, cancellationToken);
		}
		throw new TimeoutException($"找不到本次新開啟的 {GetProviderLabel(provider)} Chrome 視窗；為保護目前工作視窗，已停止視窗操作。");
	}

	private static HashSet<IntPtr> GetChromeWindowHandles()
	{
		var handles = new HashSet<IntPtr>();
		EnumWindows((handle, _) =>
		{
			if (IsChromeWindow(handle)) handles.Add(handle);
			return true;
		}, IntPtr.Zero);
		return handles;
	}

	private static IntPtr FindNewGoogleChromeWindow(HashSet<IntPtr> existingWindows, string provider, string query)
	{
		IntPtr target = IntPtr.Zero;
		string queryPrefix = query.Trim();
		if (queryPrefix.Length > 24) queryPrefix = queryPrefix[..24];
		EnumWindows((handle, _) =>
		{
			if (!IsWindowVisible(handle) || !IsChromeWindow(handle)) return true;
			var title = new StringBuilder(512);
			GetWindowText(handle, title, title.Capacity);
			string titleText = title.ToString();
			string expectedTitle = provider == "chatgpt" ? "ChatGPT" : "Google";
			// ChatGPT may replace its normal page title with the submitted prompt, so
			// identify it by a prefix of the task's own query. Chrome can reuse a
			// window handle when opening another task window, so don't rely on handle
			// novelty when the title gives us a stronger task-specific match.
			bool isThisQuery = queryPrefix.Length >= 8 && titleText.Contains(queryPrefix, StringComparison.OrdinalIgnoreCase);
			bool isNewProviderWindow = !existingWindows.Contains(handle)
				&& titleText.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase);
			if (isThisQuery || isNewProviderWindow)
			{
				target = handle;
				return false;
			}
			return true;
		}, IntPtr.Zero);
		return target;
	}

	private static string GetProviderLabel(string provider) => provider == "chatgpt" ? "ChatGPT" : "Google AI Mode";

	private static string ReadExtensionVersion()
	{
		string manifestPath = Path.Combine(AppContext.BaseDirectory, "google-ai-mode-extension", "manifest.json");
		using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
		return manifest.RootElement.GetProperty("version").GetString()
			?? throw new InvalidDataException("GGO 擴充功能 manifest 未設定版本。");
	}

	private static bool IsExtensionVersionOlder(string installedVersion, string expectedVersion) =>
		Version.TryParse(installedVersion, out var installed) && Version.TryParse(expectedVersion, out var expected) && installed < expected;

	private static bool IsChromeWindow(IntPtr handle)
	{
		var className = new StringBuilder(256);
		GetClassName(handle, className, className.Capacity);
		if (className.ToString() != "Chrome_WidgetWin_1") return false;
		var title = new StringBuilder(512);
		GetWindowText(handle, title, title.Capacity);
		return !title.ToString().Contains("DevTools", StringComparison.OrdinalIgnoreCase);
	}

	public void Dispose()
	{
		try { bridge.Stop(); } catch { }
		bridge.Dispose();
	}

	[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
	[DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
	[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr extraData);
	private sealed record BridgeJob(string Id, string Query, string Provider, string ExtensionVersion, string? ImageBase64);
	private sealed record BridgeStatus(string JobId);
	private sealed record BridgeReloadRequest(string JobId, string ExpectedVersion, string InstalledVersion);
	private sealed record BridgeReloadReady(string JobId, string ExtensionVersion);
	private sealed record BridgeResult(string JobId, string Query, string Answer, List<GoogleAiSource> Sources, string? Error);
}

public sealed record GoogleAiModeResult(string Evidence, List<GoogleAiSource> Sources, string Answer);
public sealed class GoogleAiSource
{
	public string Title { get; set; } = "";
	public string Url { get; set; } = "";
	public string Snippet { get; set; } = "";
}
