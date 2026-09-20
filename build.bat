@echo off
chcp 65001 > nul
echo ========================================================
echo  AntiGravityMeter 빌드 및 배포 스크립트 (.NET 10 / Windows)
echo ========================================================
echo.

set ROOT_DIR=%~dp0
echo [1/2] 릴리즈 빌드 중...
dotnet publish "%ROOT_DIR%AntiGravityMeter.csproj" -c Release -r win-x64 --self-contained false -o "%ROOT_DIR%Publish"
if errorlevel 1 (
    echo [ERROR] 빌드에 실패했습니다.
    pause
    exit /b 1
)

echo.
echo [2/2] 불필요한 PDB 정리 중...
del "%ROOT_DIR%Publish\*.pdb" > nul 2>&1

echo.
echo ========================================================
echo  빌드 완료!
echo  - 배포 경로: Publish\AntiGravityMeter.exe
echo ========================================================
pause
