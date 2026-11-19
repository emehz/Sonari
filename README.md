# Sonari

Mezclador de audio en tiempo real para Windows: micrófono, aplicaciones concretas, audio del sistema y sonidos, todo enviado a un micrófono virtual y con monitoreo independiente por auriculares.

> **Estado: Fase 6 (efectos de voz).** Micrófono, aplicaciones, audio del sistema y panel de sonidos mezclados en dos salidas independientes: el **micrófono virtual** (lo que oyen los demás, a través de un cable virtual como VB-CABLE) y el **monitoreo** (lo que oyes tú). El micrófono pasa por una cadena de 10 efectos en tiempo real con voces predefinidas ([docs/06-fase-6-efectos-de-voz.md](docs/06-fase-6-efectos-de-voz.md)). Todavía no hay escenas ni atajos (Fase 7). Cómo instalar el cable y configurar Discord: [docs/05-fase-5-microfono-virtual.md](docs/05-fase-5-microfono-virtual.md).

## Requisitos

| | |
|---|---|
| Windows | 10 versión 2004 (build 19041) o posterior, 64 bits. La captura por aplicación necesita esta versión |
| IDE | **Visual Studio 2026 Community** (18.x) con la carga de trabajo «Desarrollo de escritorio de .NET» |
| SDK | .NET 10 (se instala con la carga de trabajo anterior) |
| Micrófono virtual | Hasta que Sonari tenga su propio driver: un cable virtual de terceros como [VB-CABLE](https://vb-audio.com/Cable/). Cada usuario lo instala por su cuenta ([guía](docs/05-fase-5-microfono-virtual.md#instalar-vb-cable-una-vez-por-pc)) |

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
  Sonari.Audio.Effects  Efectos de voz y limitador: DSP propio, sin dependencias
  Sonari.Audio.Capture  Captura WASAPI: micrófono, aplicaciones y sistema
  Sonari.Audio.Output   Salida WASAPI (monitoreo)
  Sonari.Audio.Playback Decodificación de archivos de audio (Media Foundation)
  Sonari.Audio.Devices  Endpoints WASAPI, notificaciones de dispositivos
  Sonari.Infrastructure Ajustes en JSON con escritura atómica y rutas
tests/
  Sonari.UnitTests      Pruebas que no necesitan hardware de audio
driver/                 Driver de audio propio en C++ (WDK), en desarrollo. Ver driver/README.md
docs/
  01-viabilidad-y-arquitectura.md   Diseño completo, riesgos y plan por fases
  02-fase-2-motor-basico.md         Motor de audio: decisiones, pruebas y limitaciones
  03-fase-3-captura-por-aplicacion.md  Captura por aplicación y audio del sistema
  04-fase-4-mezclador-y-sonidos.md     Limitador, balance, silencio global y panel de sonidos
  05-fase-5-microfono-virtual.md       Micrófono virtual, VB-CABLE y Discord
  06-fase-6-efectos-de-voz.md          Cadena de efectos de voz y voces predefinidas
```

`dotnet test` usa Microsoft.Testing.Platform (configurado en `global.json`), como exige xUnit v3 con el SDK de .NET 10. El Explorador de pruebas de Visual Studio funciona sin cambios.

## Dónde guarda datos

* Configuración: `%APPDATA%\Sonari\settings.json`
* Panel de sonidos (bancos y rutas de los archivos, no los archivos en sí): `%APPDATA%\Sonari\soundboard.json`
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

Sonari es software libre: puedes usarlo, estudiarlo, modificarlo y redistribuirlo bajo los términos de la **GNU General Public License v3.0 o posterior** (GPL-3.0-or-later), incluida en [LICENSE](LICENSE). Si distribuyes una versión modificada, tiene que llevar también su código fuente bajo la GPL. Se ofrece sin ninguna garantía.

La excepción es el driver de `driver/`. Deriva de un ejemplo de Microsoft y se distribuye bajo la Microsoft Public License (MS-PL), incluida en [driver/LICENSE](driver/LICENSE). Es un programa independiente de la aplicación: solo comparte repositorio.

Las dependencias (tabla de arriba) tienen licencias MIT y Apache-2.0, compatibles con la GPL-3.0.
