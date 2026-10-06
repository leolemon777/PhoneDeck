Option Explicit
Dim shell, files, executable
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
executable = files.BuildPath(files.GetParentFolderName(WScript.ScriptFullName), "PhoneDeck.Desktop.exe")
shell.Run Chr(34) & executable & Chr(34), 0, False
