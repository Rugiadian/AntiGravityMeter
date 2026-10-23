@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ========================================================
echo  AntiGravityMeter - 윈도우 시작 메뉴 등록
echo ========================================================
echo.

set "TARGET_EXE=%~dp0Publish\AntiGravityMeter.exe"
set "ICON_FILE=%~dp0Publish\app.ico"
set "WORKING_DIR=%~dp0Publish"

if not exist "%TARGET_EXE%" (
    echo [안내] Publish 폴더에 실행 파일이 없습니다. 먼저 빌드를 진행합니다...
    call "%~dp0build.bat"
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$programs = [System.IO.Path]::Combine($env:APPDATA, 'Microsoft\Windows\Start Menu\Programs'); $wsh = New-Object -ComObject WScript.Shell; $sc = $wsh.CreateShortcut((Join-Path $programs 'AntiGravityMeter.lnk')); $sc.TargetPath = '%TARGET_EXE%'; $sc.WorkingDirectory = '%WORKING_DIR%'; $sc.IconLocation = '%ICON_FILE%'; $sc.Description = 'AntiGravityMeter (Google Antigravity & Gemini 실시간 쿼터 위젯)'; $sc.Save(); Write-Host '[성공] 윈도우 시작 메뉴에 바로가기가 정상 등록되었습니다!' -ForegroundColor Green"

echo.
pause
