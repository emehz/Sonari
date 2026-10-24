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
echo.
echo Si devcon pide reiniciar, reinicia la maquina virtual.
pause
