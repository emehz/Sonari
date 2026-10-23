# Sonari

Mezclador de audio en tiempo real para Windows: micrófono, aplicaciones concretas, audio del sistema y sonidos, todo enviado a un micrófono virtual y con monitoreo independiente por auriculares.

> **Estado: Fase 3 (captura por aplicación).** Micrófono, aplicaciones concretas y audio del sistema, cada uno como canal independiente, con compensación de deriva, medidores y reconexión automática. Todavía no hay reproductor de sonidos (Fase 4) ni micrófono virtual (Fase 5). Detalles en [docs/02-fase-2-motor-basico.md](docs/02-fase-2-motor-basico.md) y [docs/03-fase-3-captura-por-aplicacion.md](docs/03-fase-3-captura-por-aplicacion.md).

## Requisitos

| | |
|---|---|
| Windows | 10 versión 2004 (build 19041) o posterior, 64 bits. La captura por aplicación necesita esta versión |
| IDE | **Visual Studio 2026 Community** (18.x) con la carga de trabajo «Desarrollo de escritorio de .NET» |
| SDK | .NET 10 (se instala con la carga de trabajo anterior) |
| Micrófono virtual | Hasta que Sonari tenga su propio driver (después de la Fase 5): un cable virtual de terceros como [VB-CABLE](https://vb-audio.com/Cable/). Cada usuario lo instala por su cuenta |

¿Por qué no Visual Studio 2022? Porque solo puede compilar hasta .NET 9, y tanto .NET 8 como .NET 9 dejan de tener soporte el 10/11/2026. Si lo necesitas de forma temporal, cambia `SonariNet` a `net9.0` en [Directory.Build.props](Directory.Build.props).

## Compilar y ejecutar

> ⚠️ **Clona el repositorio en una ruta corta** (p. ej. `C:\dev\Sonari`). El compilador XAML de WPF falla en silencio si alguna ruta de `obj\` supera los 260 caracteres. El síntoma es *«No se encuentra el recurso 'views/mainwindow.xaml'»* al arrancar.

**Visual Studio:**

1. Abre `Sonari.sln`.
2. Haz clic derecho en `Sonari.App` → **Establecer como proyecto de inicio**.
3. Pulsa **F5**.

**Línea de comandos:**

```
dotnet build Sonari.sln
dotnet test Sonari.sln
dotnet run --project src/Sonari.App
```

## Estructura

```
src/
  Sonari.App            Ejecutable WPF y composición (DI, logging, instancia única)
  Sonari.Presentation   Vistas XAML, ViewModels (MVVM) y tema oscuro
  Sonari.Audio.Core     Contratos, formatos, buffers lock-free (sin dependencias)
  Sonari.Audio.Engine   Grafo de mezcla, compensación de deriva, supervisor (sin NAudio)
  Sonari.Audio.Capture  Captura WASAPI: micrófono, aplicaciones y sistema
  Sonari.Audio.Output   Salida WASAPI (monitoreo)
  Sonari.Audio.Devices  Endpoints WASAPI, notificaciones de dispositivos
  Sonari.Infrastructure Ajustes en JSON con escritura atómica y rutas
tests/
  Sonari.UnitTests      Pruebas que no necesitan hardware de audio
docs/
  01-viabilidad-y-arquitectura.md   Diseño completo, riesgos y plan por fases
  02-fase-2-motor-basico.md         Motor de audio: decisiones, pruebas y limitaciones
  03-fase-3-captura-por-aplicacion.md  Captura por aplicación y audio del sistema
```

`dotnet test` usa Microsoft.Testing.Platform (configurado en `global.json`), como exige xUnit v3 con el SDK de .NET 10. El Explorador de pruebas de Visual Studio funciona sin cambios.

## Dónde guarda datos

* Configuración: `%APPDATA%\Sonari\settings.json`
* Registros técnicos (sin audio ni datos personales): `%LOCALAPPDATA%\Sonari\logs\`

Sonari nunca graba ni guarda audio sin una acción explícita del usuario, y solo captura las fuentes que el usuario selecciona.

## Dependencias

| Paquete | Licencia |
|---|---|
| CommunityToolkit.Mvvm 8.4.2 | MIT |
| Microsoft.Extensions.Hosting 10.0.13 | MIT |
| NAudio.Wasapi 3.3.0 | MIT |
| Serilog.Extensions.Logging 10.0.0 · Serilog.Sinks.File 7.0.0 | Apache-2.0 |
| xunit.v3 4.0.2 (solo pruebas) | Apache-2.0 |

## Licencia

Por decidir.
