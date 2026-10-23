# OndaMix

Mezclador de audio en tiempo real para Windows: micrófono, aplicaciones concretas, audio del sistema y sonidos, todo enviado a un micrófono virtual y con monitoreo independiente por auriculares.

> **Estado: Fase 2 (motor básico).** Micrófono → auriculares en tiempo real, con compensación de deriva de reloj, medidores, diagnóstico y recuperación cuando un dispositivo se desconecta. Todavía no hay captura por aplicación (Fase 3), reproductor de sonidos (Fase 4) ni micrófono virtual (Fase 5). Detalles en [docs/02-fase-2-motor-basico.md](docs/02-fase-2-motor-basico.md).

## Requisitos

| | |
|---|---|
| Windows | 10 versión 2004 (build 19041) o posterior, 64 bits. La captura por aplicación necesita esta versión |
| IDE | **Visual Studio 2026 Community** (18.x) con la carga de trabajo «Desarrollo de escritorio de .NET» |
| SDK | .NET 10 (se instala con la carga de trabajo anterior) |
| Micrófono virtual | Hasta que OndaMix tenga su propio driver (después de la Fase 5): un cable virtual de terceros como [VB-CABLE](https://vb-audio.com/Cable/). Cada usuario lo instala por su cuenta |

¿Por qué no Visual Studio 2022? Porque solo puede compilar hasta .NET 9, y tanto .NET 8 como .NET 9 dejan de tener soporte el 10/11/2026. Si lo necesitas de forma temporal, cambia `OndaNet` a `net9.0` en [Directory.Build.props](Directory.Build.props).

## Compilar y ejecutar

> ⚠️ **Clona el repositorio en una ruta corta** (p. ej. `C:\dev\OndaMix`). El compilador XAML de WPF falla en silencio si alguna ruta de `obj\` supera los 260 caracteres. El síntoma es *«No se encuentra el recurso 'views/mainwindow.xaml'»* al arrancar.

**Visual Studio:**

1. Abre `OndaMix.sln`.
2. Haz clic derecho en `OndaMix.App` → **Establecer como proyecto de inicio**.
3. Pulsa **F5**.

**Línea de comandos:**

```
dotnet build OndaMix.sln
dotnet test OndaMix.sln
dotnet run --project src/OndaMix.App
```

## Estructura

```
src/
  OndaMix.App            Ejecutable WPF y composición (DI, logging, instancia única)
  OndaMix.Presentation   Vistas XAML, ViewModels (MVVM) y tema oscuro
  OndaMix.Audio.Core     Contratos, formatos, buffers lock-free (sin dependencias)
  OndaMix.Audio.Engine   Grafo de mezcla, compensación de deriva, supervisor (sin NAudio)
  OndaMix.Audio.Capture  Captura WASAPI (micrófono)
  OndaMix.Audio.Output   Salida WASAPI (monitoreo)
  OndaMix.Audio.Devices  Endpoints WASAPI, notificaciones de dispositivos
  OndaMix.Infrastructure Ajustes en JSON con escritura atómica y rutas
tests/
  OndaMix.UnitTests      Pruebas que no necesitan hardware de audio
docs/
  01-viabilidad-y-arquitectura.md   Diseño completo, riesgos y plan por fases
  02-fase-2-motor-basico.md         Motor de audio: decisiones, pruebas y limitaciones
```

`dotnet test` usa Microsoft.Testing.Platform (configurado en `global.json`), como exige xUnit v3 con el SDK de .NET 10. El Explorador de pruebas de Visual Studio funciona sin cambios.

## Dónde guarda datos

* Configuración: `%APPDATA%\OndaMix\settings.json`
* Registros técnicos (sin audio ni datos personales): `%LOCALAPPDATA%\OndaMix\logs\`

OndaMix nunca graba ni guarda audio sin una acción explícita del usuario, y solo captura las fuentes que el usuario selecciona.

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
