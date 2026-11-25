Option Explicit
Dim shell, fso, root, executable, buildScript, powershell, exitCode, command
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
executable = fso.BuildPath(root, "AsuisuiLLM-" & ChrW(35222) & ChrW(31383) & ChrW(29256) & "-v7\DesktopAI.Gui.exe")
buildScript = fso.BuildPath(root, "Build-And-Launch.ps1")
powershell = shell.ExpandEnvironmentStrings("%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")
If Not fso.FileExists(buildScript) Then
    MsgBox "The automatic build script was not found: " & buildScript, 16, "Asuisui AI"
    WScript.Quit 1
End If
command = Chr(34) & powershell & Chr(34) & " -NoProfile -ExecutionPolicy Bypass -File " & Chr(34) & buildScript & Chr(34)
exitCode = shell.Run(command, 0, True)
If exitCode <> 0 Then
    MsgBox "Asuisui AI could not be built, so the application was not started." & vbCrLf & _
        "Build log: " & shell.ExpandEnvironmentStrings("%TEMP%\AsuisuiAI-build.log"), 16, "Asuisui AI"
    WScript.Quit exitCode
End If
If Not fso.FileExists(executable) Then
    MsgBox "Build finished, but the Asuisui AI executable was not found: " & executable, 16, "Asuisui AI"
    WScript.Quit 1
End If
shell.Run Chr(34) & executable & Chr(34), 1, False
