$ErrorActionPreference = "Stop"

$binDirectory = Join-Path $PSScriptRoot "bin"
$projectExecutables = @(
	Get-ChildItem -LiteralPath $binDirectory -Filter "DesktopAI.exe" -File -Recurse -ErrorAction SilentlyContinue |
		Select-Object -ExpandProperty FullName
)

if ($projectExecutables.Count -eq 0) {
	Write-Host "找不到 bin 目錄中的 DesktopAI.exe。"
	exit 1
}

$targets = @(
	Get-CimInstance Win32_Process -Filter "Name = 'DesktopAI.exe'" |
		Where-Object { $_.ExecutablePath -in $projectExecutables }
)

if ($targets.Count -eq 0) {
	Write-Host "沒有找到正在執行的本專案 DesktopAI.exe。"
	exit 0
}

$stopped = [System.Collections.Generic.List[int]]::new()
$failed = [System.Collections.Generic.List[int]]::new()

foreach ($target in $targets) {
	$id = [int]$target.ProcessId
	try {
		$process = [System.Diagnostics.Process]::GetProcessById($id)
		[void]$process.CloseMainWindow()
		if (-not $process.WaitForExit(1000)) {
			$process.Kill()
			if (-not $process.WaitForExit(2000)) {
				$failed.Add($id)
				continue
			}
		}
		$stopped.Add($id)
	}
	catch {
		$failed.Add($id)
	}
}

Write-Host "已停止本專案 DesktopAI.exe：$($stopped.Count) 個。"
if ($stopped.Count -gt 0) { Write-Host "PID：$($stopped -join ', ')" }
if ($failed.Count -gt 0) {
	Write-Host "停止失敗的 PID：$($failed -join ', ')" -ForegroundColor Red
	exit 1
}
