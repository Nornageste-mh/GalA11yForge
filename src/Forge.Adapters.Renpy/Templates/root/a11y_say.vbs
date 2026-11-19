' GalA11y Demo - SAPI speech bridge (generated file, ASCII only on purpose).
' Usage: wscript.exe a11y_say.vbs <text-file UTF-16LE> [rate -10..10]
' The text file is written by the overlay (01_speech.rpy) and deleted here.
' 0.2.1 fix (W2-P): use *synchronous* Speak with no flags. The old version used
' flags 3 (SVSFlagsAsync|SVSFPurgeBeforeSpeak); because wscript exits as soon as
' the script ends, the async voice was torn down with the process and the game
' was effectively silent (A/B: endpoint peak 0.000 vs 0.491 after sync fix).
' Ren'Py's own lib/<pyver>/say.vbs also calls Speak synchronously.
Option Explicit

Dim fso, argc, path, rate, text, f, sapi

Set fso = CreateObject("Scripting.FileSystemObject")
argc = WScript.Arguments.Count
If argc < 1 Then WScript.Quit 2

path = WScript.Arguments(0)
If Not fso.FileExists(path) Then WScript.Quit 3

Set f = fso.OpenTextFile(path, 1, False, -1)   ' -1 = Unicode (UTF-16LE)
text = f.ReadAll
f.Close

On Error Resume Next
fso.DeleteFile path, True
On Error GoTo 0

If Len(text) = 0 Then WScript.Quit 0

Set sapi = CreateObject("SAPI.SpVoice")
If argc >= 2 Then
    rate = CInt(WScript.Arguments(1))
    If rate < -10 Then rate = -10
    If rate > 10 Then rate = 10
    sapi.Rate = rate
End If
sapi.Speak text
