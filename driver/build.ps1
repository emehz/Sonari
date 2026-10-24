<#
.SYNOPSIS
    Compila el driver y deja en driver\out todo lo necesario para instalarlo en la máquina virtual.

.DESCRIPTION
    1. Compila SonariAudio.sln (x64) con el MSBuild de Visual Studio.
    2. Copia a driver\out el paquete firmado (.inf, .sys, .cat), el certificado de prueba con el que se firmó,
       devcon.exe del WDK y los scripts de instalación de la carpeta vm\.

    driver\out está en .gitignore: el certificado y devcon.exe no se suben al repositorio.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' |
    Select-Object -First 1
if (-not $msbuild) { throw 'No se encontró MSBuild. ¿Está instalado Visual Studio 2026 con C++?' }

& $msbuild (Join-Path $root 'SonariAudio.sln') /m /nologo /v:minimal "/p:Configuration=$Configuration" /p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "La compilación falló (código $LASTEXITCODE)." }

$package = Join-Path $root "x64\$Configuration\package"
$certificate = Join-Path $root "x64\$Configuration\package.cer"
$devcon = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Tools\10.0.28000.0\x64\devcon.exe'
foreach ($required in $package, $certificate, $devcon) {
    if (-not (Test-Path $required)) { throw "Falta $required" }
}

$out = Join-Path $root 'out'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

Copy-Item (Join-Path $package '*') $out
Copy-Item $certificate (Join-Path $out 'SonariTest.cer')
Copy-Item $devcon $out
Copy-Item (Join-Path $root 'vm\*') $out

Write-Host ''
Write-Host "Listo: $out" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, Length -AutoSize
