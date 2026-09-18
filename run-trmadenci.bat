@echo off
setlocal
title TrMadenci

cd /d "%~dp0"

set "PROJECT=src\TrMadenci.Service\TrMadenci.Service.csproj"
set "CONFIG=trmadenci.json"
set "SOLUTION=TrMadenci.sln"

:MENU
cls
echo ==========================================
echo              TrMadenci
echo ==========================================
echo.
echo   1 - Probe + Native Probe
echo   2 - Mining Baslat
echo   3 - Release Build
echo   4 - Clean + Release Build
echo   5 - Cikis
echo.
echo ==========================================
set /p "CHOICE=Seciminiz: "

if "%CHOICE%"=="1" goto PROBE
if "%CHOICE%"=="2" goto MINE
if "%CHOICE%"=="3" goto BUILD
if "%CHOICE%"=="4" goto CLEANBUILD
if "%CHOICE%"=="5" goto END

goto MENU


:PROBE
cls
echo ==========================================
echo Probe + Native Probe baslatiliyor...
echo ==========================================
echo.

dotnet run ^
  --project "%PROJECT%" ^
  -c Release ^
  -- "%CONFIG%" --probe --native-probe

echo.
echo ==========================================
echo Islem tamamlandi.
echo ==========================================
pause
goto MENU


:MINE
cls
echo ==========================================
echo TrMadenci Mining baslatiliyor...
echo CTRL+C ile durdurabilirsiniz.
echo ==========================================
echo.

dotnet run ^
  --project "%PROJECT%" ^
  -c Release ^
  -- "%CONFIG%" --mine

echo.
echo ==========================================
echo Mining islemi sona erdi.
echo ==========================================
pause
goto MENU


:BUILD
cls
echo ==========================================
echo Release Build baslatiliyor...
echo ==========================================
echo.

dotnet build "%SOLUTION%" -c Release

if errorlevel 1 (
    echo.
    echo BUILD BASARISIZ.
) else (
    echo.
    echo BUILD BASARILI.
)

echo.
pause
goto MENU


:CLEANBUILD
cls
echo ==========================================
echo Clean + Release Build baslatiliyor...
echo ==========================================
echo.

echo [1/2] Clean...
dotnet clean "%SOLUTION%" -c Release

if errorlevel 1 (
    echo.
    echo CLEAN BASARISIZ.
    pause
    goto MENU
)

echo.
echo [2/2] Build...
dotnet build "%SOLUTION%" -c Release

if errorlevel 1 (
    echo.
    echo BUILD BASARISIZ.
) else (
    echo.
    echo CLEAN + BUILD BASARILI.
)

echo.
pause
goto MENU


:END
endlocal
exit /b 0