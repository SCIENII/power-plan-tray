@echo off
setlocal

rem Build PowerPlanTray.exe with the .NET Framework compiler that ships with Windows.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

rem Close the running tray app first, otherwise the exe file is locked.
taskkill /f /im PowerPlanTray.exe >nul 2>nul

"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:PowerPlanTray.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\PowerPlanTray.cs
if errorlevel 1 goto failed

echo [OK] PowerPlanTray.exe
exit /b 0

:nocsc
echo [x] csc.exe not found under %WINDIR%\Microsoft.NET
exit /b 1

:failed
echo [x] build failed
exit /b 1