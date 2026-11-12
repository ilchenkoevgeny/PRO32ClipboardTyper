@echo off
setlocal
cd /d "%~dp0"

dotnet publish .\PRO32ClipboardTyper\PRO32ClipboardTyper.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)

echo.
echo EXE: PRO32ClipboardTyper\bin\Release\net8.0-windows\win-x64\publish\PRO32ClipboardTyper.exe
pause
