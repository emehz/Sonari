@echo off
rem Quita el dispositivo de prueba. El paquete sigue en el almacen de drivers de Windows;
rem para dejar la maquina virtual limpia del todo, restaura la instantanea.
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
    echo Ejecuta este archivo como administrador: clic derecho, "Ejecutar como administrador".
    pause
    exit /b 1
)

devcon.exe remove Root\SonariVirtualAudio

rem devcon: 0 = quitado, 1 = quitado al reiniciar (algo lo tenia abierto), 2 o mas = fallo.
echo.
if errorlevel 2 (
    echo ERROR: no se pudo quitar el dispositivo.
) else if errorlevel 1 (
    echo IMPORTANTE: el motor de audio de Windows tenia el dispositivo abierto y se
    echo quitara al reiniciar. REINICIA la maquina virtual antes de volver a instalar.
) else (
    echo Dispositivo quitado.
)
pause
