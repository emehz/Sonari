@echo off
rem Instala el driver de prueba en la maquina virtual. Ejecutar como administrador desde una carpeta local
rem (no desde la carpeta compartida de VirtualBox).
setlocal
cd /d "%~dp0"
set HWID=Root\SonariVirtualAudio
set INF=SonariAudio.inf

net session >nul 2>&1
if errorlevel 1 (
    echo Ejecuta este archivo como administrador: clic derecho, "Ejecutar como administrador".
    pause
    exit /b 1
)

rem Windows solo carga drivers con firma de prueba si arranco en modo de prueba. SystemStartOptions refleja
rem el arranque actual: no basta con haber ejecutado bcdedit, hay que reiniciar despues.
reg query HKLM\SYSTEM\CurrentControlSet\Control /v SystemStartOptions | find /i "TESTSIGNING" >nul
if errorlevel 1 (
    echo Windows no esta en modo de prueba y rechazaria el driver con el codigo 52.
    echo Ejecuta como administrador:  bcdedit /set testsigning on
    echo Reinicia y vuelve a ejecutar este archivo. Debe verse "Modo de prueba" en el escritorio.
    pause
    exit /b 1
)

echo [1/3] Confiando en el certificado de prueba de Sonari...
certutil -addstore -f Root SonariTest.cer >nul || goto :error
certutil -addstore -f TrustedPublisher SonariTest.cer >nul || goto :error

echo [2/3] Comprobando si el dispositivo ya existe...
devcon.exe find %HWID% | find /i "Root\" >nul
if not errorlevel 1 (
    echo El dispositivo ya existe: actualizando el driver.
    devcon.exe update %INF% %HWID%
    goto :result
)

echo [3/3] Instalando el dispositivo %HWID%...
devcon.exe install %INF% %HWID%

:result
rem devcon: 0 = correcto, 1 = correcto pero hay que reiniciar, 2 o mas = fallo.
if errorlevel 2 goto :error
echo.
if errorlevel 1 (
    echo Instalado. Windows pide reiniciar la maquina virtual para terminar.
) else (
    echo Instalado. Revisa Panel de control - Sonido.
)
pause
exit /b 0

:error
echo.
echo ERROR: la instalacion fallo. Revisa C:\Windows\INF\setupapi.dev.log (lo mas reciente esta al final).
pause
exit /b 1
