@echo off
rem Rebuilds bright.exe (CLI) and BrightTray.exe (tray app) using the C# compiler
rem that ships with Windows (.NET Framework 4.8). No SDK or Visual Studio needed.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /optimize+ /define:CLI  /target:exe    /out:"%~dp0bright.exe"     /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0Program.cs" || exit /b 1
"%CSC%" /nologo /optimize+ /define:TRAY /target:winexe /out:"%~dp0BrightTray.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0Program.cs" || exit /b 1
echo Built: bright.exe, BrightTray.exe
