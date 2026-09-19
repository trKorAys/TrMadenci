@echo off
setlocal EnableExtensions
title TrMadenci

cd /d "%~dp0"

set "PROJECT=src\TrMadenci.Service\TrMadenci.Service.csproj"
set "SOLUTION=TrMadenci.sln"
set "APP=%~dp0TrMadenci.Service.exe"
if exist "%APP%" (
    set "RUNTIME_MODE=PACKAGE"
) else (
    set "RUNTIME_MODE=SOURCE"
)
set "CONFIG="
set "COIN_NAME="
set "ACTION_TITLE="
set "ACTION_ARGS="
set "RETURN_MENU=COIN_MENU"

:COIN_MENU
cls
echo ================================================
echo                  TrMadenci
echo ================================================
echo.
echo   Madencilik yapilacak coin'i secin:
echo.
echo   1 - Ravencoin         RVN / KAWPOW   [HAZIR]
echo   2 - Ethereum Classic ETC / ETCHASH   [SOAK GEREKLI]
echo   3 - Conflux           CFX / OCTOPUS   [YETERLILIK GEREKLI]
echo   4 - Monero            XMR / RANDOMX   [GELISTIRME]
echo.
if /I "%RUNTIME_MODE%"=="SOURCE" (
    echo   5 - Derleme islemleri
) else (
    echo   5 - Hakkinda ve destek
)
echo   6 - Cikis
echo.
echo ================================================
set "CHOICE="
set /p "CHOICE=Seciminiz: "
if not defined CHOICE goto END

if "%CHOICE%"=="1" goto SELECT_RVN
if "%CHOICE%"=="2" goto SELECT_ETC
if "%CHOICE%"=="3" goto SELECT_CFX
if "%CHOICE%"=="4" goto SELECT_XMR
if "%CHOICE%"=="5" (
    if /I "%RUNTIME_MODE%"=="SOURCE" goto BUILD_MENU
    goto ABOUT
)
if "%CHOICE%"=="6" goto END
goto COIN_MENU

:SELECT_RVN
set "CONFIG=trmadenci.json"
set "COIN_NAME=Ravencoin (RVN) / KAWPOW"
if not exist "%CONFIG%" (
    set "MISSING_TEMPLATE=trmadenci.example.json"
    goto CONFIG_MISSING
)
goto RVN_MENU

:SELECT_ETC
set "CONFIG=trmadenci.etc.local.json"
set "COIN_NAME=Ethereum Classic (ETC) / ETCHASH"
if not exist "%CONFIG%" (
    set "MISSING_TEMPLATE=trmadenci.etc.example.json"
    goto CONFIG_MISSING
)
goto ETC_MENU

:SELECT_CFX
set "CONFIG=trmadenci.cfx.local.json"
set "COIN_NAME=Conflux (CFX) / OCTOPUS"
if not exist "%CONFIG%" (
    set "MISSING_TEMPLATE=trmadenci.cfx.example.json"
    goto CONFIG_MISSING
)
goto CFX_MENU

:SELECT_XMR
set "COIN_NAME=Monero (XMR) / RANDOMX"
if exist "trmadenci.xmr.local.json" (
    set "CONFIG=trmadenci.xmr.local.json"
) else (
    set "CONFIG=trmadenci.xmr.example.json"
)
goto XMR_MENU

:RVN_MENU
cls
echo ================================================
echo   Secili coin : %COIN_NAME%
echo   Ayar dosyasi: %CONFIG%
echo   Durum       : Uretim madenciligi hazir
echo ================================================
echo.
echo   1 - Supervisor ile madenciligi baslat [ONERILEN]
echo   2 - Madenciligi dogrudan baslat
echo   3 - Pool + native GPU kontrolu
echo   4 - GPU secimini degistir
echo   5 - Coin secimine don
echo   6 - Cikis
echo.
set "CHOICE="
set /p "CHOICE=Seciminiz: "
if not defined CHOICE goto END

if "%CHOICE%"=="1" (
    set "ACTION_TITLE=RVN/KAWPOW Supervisor madenciligi"
    set "ACTION_ARGS=--mine --supervise"
    set "RETURN_MENU=RVN_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="2" (
    set "ACTION_TITLE=RVN/KAWPOW madenciligi"
    set "ACTION_ARGS=--mine"
    set "RETURN_MENU=RVN_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="3" (
    set "ACTION_TITLE=RVN pool ve native GPU kontrolu"
    set "ACTION_ARGS=--probe --native-probe"
    set "RETURN_MENU=RVN_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="4" (
    set "ACTION_TITLE=RVN GPU secimi"
    set "ACTION_ARGS=--select-gpus"
    set "RETURN_MENU=RVN_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="5" goto COIN_MENU
if "%CHOICE%"=="6" goto END
goto RVN_MENU

:ETC_MENU
cls
echo ================================================
echo   Secili coin : %COIN_NAME%
echo   Ayar dosyasi: %CONFIG%
echo   Durum       : Uretim oncesi son soak bekleniyor
echo ================================================
echo.
echo   1 - 24 saatlik Supervisor soak madenciligini baslat
echo   2 - Tek-share CUDA yeterlilik madenciligini baslat
echo   3 - Pool + native GPU kontrolu
echo   4 - GPU secimini degistir
echo   5 - Coin secimine don
echo   6 - Cikis
echo.
set "CHOICE="
set /p "CHOICE=Seciminiz: "
if not defined CHOICE goto END

if "%CHOICE%"=="1" (
    set "ACTION_TITLE=ETC/ETCHASH 24 saatlik soak madenciligi"
    set "ACTION_ARGS=--mine --supervise --soak-hours=24"
    set "RETURN_MENU=ETC_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="2" (
    set "ACTION_TITLE=ETC/ETCHASH tek-share CUDA yeterlilik madenciligi"
    set "ACTION_ARGS=--mine --etchash-qualification"
    set "RETURN_MENU=ETC_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="3" (
    set "ACTION_TITLE=ETC pool ve native GPU kontrolu"
    set "ACTION_ARGS=--probe --native-probe"
    set "RETURN_MENU=ETC_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="4" (
    set "ACTION_TITLE=ETC GPU secimi"
    set "ACTION_ARGS=--select-gpus"
    set "RETURN_MENU=ETC_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="5" goto COIN_MENU
if "%CHOICE%"=="6" goto END
goto ETC_MENU

:CFX_MENU
cls
echo ================================================
echo   Secili coin : %COIN_NAME%
echo   Ayar dosyasi: %CONFIG%
echo   Durum       : GPU ve canli-share yeterliligi bekleniyor
echo ================================================
echo.
echo   1 - Tek-share Octopus yeterlilik madenciligini baslat
echo   2 - Pool + native GPU kontrolu
echo   3 - Hafif Octopus vektor testlerini calistir
echo   4 - GPU secimini degistir
echo   5 - Coin secimine don
echo   6 - Cikis
echo.
set "CHOICE="
set /p "CHOICE=Seciminiz: "
if not defined CHOICE goto END

if "%CHOICE%"=="1" (
    set "ACTION_TITLE=CFX/OCTOPUS tek-share yeterlilik madenciligi"
    set "ACTION_ARGS=--mine --octopus-qualification"
    set "RETURN_MENU=CFX_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="2" (
    set "ACTION_TITLE=CFX pool ve native GPU kontrolu"
    set "ACTION_ARGS=--probe --native-probe"
    set "RETURN_MENU=CFX_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="3" (
    set "ACTION_TITLE=CFX/OCTOPUS hafif vektor testleri"
    set "ACTION_ARGS=--octopus-multipoint-self-test --octopus-cuda-self-test"
    set "RETURN_MENU=CFX_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="4" (
    set "ACTION_TITLE=CFX GPU secimi"
    set "ACTION_ARGS=--select-gpus"
    set "RETURN_MENU=CFX_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="5" goto COIN_MENU
if "%CHOICE%"=="6" goto END
goto CFX_MENU

:XMR_MENU
cls
echo ================================================
echo   Secili coin : %COIN_NAME%
echo   Ayar dosyasi: %CONFIG%
echo   Durum       : CPU worker ve developer XMR cuzdan yolu bekleniyor
echo ================================================
echo.
echo   XMR madenciligi henuz acik degildir.
echo.
echo   1 - RandomX CPU resmi vektor testini calistir
echo   2 - Coin secimine don
echo   3 - Cikis
echo.
set "CHOICE="
set /p "CHOICE=Seciminiz: "
if not defined CHOICE goto END

if "%CHOICE%"=="1" (
    set "ACTION_TITLE=XMR/RANDOMX CPU resmi vektor testi"
    set "ACTION_ARGS=--randomx-self-test"
    set "RETURN_MENU=XMR_MENU"
    goto EXECUTE
)
if "%CHOICE%"=="2" goto COIN_MENU
if "%CHOICE%"=="3" goto END
goto XMR_MENU

:BUILD_MENU
cls
echo ================================================
echo               Derleme islemleri
echo ================================================
echo.
echo   1 - Release Build
echo   2 - Clean + Release Build
echo   3 - Coin secimine don
echo   4 - Cikis
echo.
set "CHOICE="
set /p "CHOICE=Seciminiz: "
if not defined CHOICE goto END

if "%CHOICE%"=="1" goto BUILD
if "%CHOICE%"=="2" goto CLEANBUILD
if "%CHOICE%"=="3" goto COIN_MENU
if "%CHOICE%"=="4" goto END
goto BUILD_MENU

:ABOUT
cls
echo ================================================
echo              TrMadenci Hakkinda
echo ================================================
echo.
echo   Destek : koray.altiner@outlook.com
echo   Web    : https://www.yatirimiq.com
echo.
echo   Gelistirici ucreti: %%0,75
echo   Ucret, secilen coin ve algoritma uzerinde
echo   seffaf ve rastgele zaman araliklariyla uygulanir.
echo.
echo   1 - Coin secimine don
echo   2 - Cikis
echo.
set "CHOICE="
set /p "CHOICE=Seciminiz: "
if not defined CHOICE goto END
if "%CHOICE%"=="1" goto COIN_MENU
if "%CHOICE%"=="2" goto END
goto ABOUT

:EXECUTE
cls
echo ================================================
echo   %ACTION_TITLE%
echo ================================================
echo   Coin  : %COIN_NAME%
echo   Ayar  : %CONFIG%
echo   Komut : %ACTION_ARGS%
echo.
echo   Madencilik baslarsa P: duraklat, S: devam,
echo   D: durum, CTRL+C: guvenli kapatma.
echo ================================================
echo.

if /I "%RUNTIME_MODE%"=="PACKAGE" goto EXECUTE_PACKAGE

dotnet run ^
  --project "%PROJECT%" ^
  -c Release ^
  -- "%CONFIG%" %ACTION_ARGS%
goto EXECUTE_DONE

:EXECUTE_PACKAGE
"%APP%" "%CONFIG%" %ACTION_ARGS%

:EXECUTE_DONE

set "RESULT=%ERRORLEVEL%"
echo.
echo ================================================
if "%RESULT%"=="0" (
    echo   Islem normal olarak tamamlandi.
) else (
    echo   Islem hata kodu %RESULT% ile sona erdi.
)
echo ================================================
pause
goto %RETURN_MENU%

:CONFIG_MISSING
cls
echo ================================================
echo   Yapilandirma dosyasi bulunamadi
echo ================================================
echo.
echo   Coin    : %COIN_NAME%
echo   Beklenen: %CONFIG%
echo   Sablon  : %MISSING_TEMPLATE%
echo.
echo   Once sablonu yerel dosyaya kopyalayin ve kendi
echo   pool/worker bilgilerinizi girin:
echo.
echo   copy "%MISSING_TEMPLATE%" "%CONFIG%"
echo.
echo   Ornek dosyadaki placeholder bilgilerle madencilik
echo   otomatik olarak baslatilmaz.
echo.
pause
goto COIN_MENU

:BUILD
cls
echo ================================================
echo   Release Build baslatiliyor...
echo ================================================
echo.

dotnet build "%SOLUTION%" -c Release
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
    echo BUILD BASARILI.
) else (
    echo BUILD BASARISIZ. Hata kodu: %RESULT%
)
echo.
pause
goto BUILD_MENU

:CLEANBUILD
cls
echo ================================================
echo   Clean + Release Build baslatiliyor...
echo ================================================
echo.

echo [1/2] Clean...
dotnet clean "%SOLUTION%" -c Release
if errorlevel 1 (
    echo.
    echo CLEAN BASARISIZ.
    pause
    goto BUILD_MENU
)

echo.
echo [2/2] Build...
dotnet build "%SOLUTION%" -c Release
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
    echo CLEAN + BUILD BASARILI.
) else (
    echo BUILD BASARISIZ. Hata kodu: %RESULT%
)
echo.
pause
goto BUILD_MENU

:END
endlocal
exit /b 0
