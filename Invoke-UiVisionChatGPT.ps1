param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Question,

    [string]$MacroName = 'SendChatGPT.js',

    [string]$UserDataDirectory = '',

    [string]$ProfileName = 'Profile 2'
)

$ErrorActionPreference = 'Stop'

$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$userData = if ([string]::IsNullOrWhiteSpace($UserDataDirectory)) { Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data' } else { $UserDataDirectory }
$profileName = $ProfileName
$launcherPath = Join-Path $PSScriptRoot 'RPA\ui.vision.html'

if (-not (Test-Path -LiteralPath $chrome)) {
    throw "找不到 Chrome：$chrome"
}
if (-not (Test-Path -LiteralPath $launcherPath)) {
    throw "找不到 UI.Vision 啟動頁：$launcherPath"
}

$launcherUri = ([System.Uri]$launcherPath).AbsoluteUri
$encodedQuestion = [System.Uri]::EscapeDataString($Question)
$encodedMacro = [System.Uri]::EscapeDataString($MacroName)
$url = "${launcherUri}?direct=1&macro=$encodedMacro&cmd_var1=$encodedQuestion&closeRPA=0&closeBrowser=0"

# 使用 AI 提問流程指定的 GGO Chrome 設定檔；Autostart 頁會呼叫已儲存的 Ui.Vision 巨集。
$argumentLine = '--user-data-dir="' + $userData + '" --profile-directory="' + $profileName + '" --new-window "' + $url + '"'
$process = Start-Process -FilePath $chrome -ArgumentList $argumentLine -PassThru
Write-Output "Ui.Vision 啟動頁已交給 Chrome（PID $($process.Id)）。"

