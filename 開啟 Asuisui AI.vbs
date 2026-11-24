Option Explicit
Dim shell, fso, root, executable
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
executable = fso.BuildPath(root, "AsuisuiLLM-" & ChrW(35222) & ChrW(31383) & ChrW(29256) & "-v7\DesktopAI.Gui.exe")
If Not fso.FileExists(executable) Then
    MsgBox "The Asuisui AI application was not found. Please build DesktopAI.Gui first.", 48, "Asuisui AI"
    WScript.Quit 1
End If
shell.Run Chr(34) & executable & Chr(34), 1, False
