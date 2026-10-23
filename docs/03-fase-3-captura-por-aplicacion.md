# Fase 3: captura de audio por aplicación

**Objetivo:** elegir aplicaciones concretas y capturar únicamente su audio, cada una como canal independiente del mezclador, y ofrecer «audio del sistema» sin realimentación.

## Qué se añadió

| Proyecto | Contenido |
|---|---|
| `Sonari.Audio.Core` | `ISourceChannel`, `SourceKind`, `SourceStatus`, `ApplicationTarget`, `ChannelMetrics`, `IProcessCaptureStreamFactory`, `IProcessLocator` e `IAudioApplicationCatalog`. `IAudioEngine` admite ahora fuentes dinámicas (`Sources`, `AddApplicationSource`, `AddSystemAudioSource`, `RemoveSource`) |
| `Sonari.Audio.Engine` | `SourceChannel` (identidad, controles y estado de cada fuente), `ProcessCaptureRunner` (un bucle por aplicación: buscar el proceso → capturar → vigilar → reenganchar) y un `MonitorBus` con lista de canales intercambiable sin bloqueos. `MixerChannel` distingue vaciados reales de pausas de la aplicación |
| `Sonari.Audio.Capture` | `WasapiProcessCaptureStreamFactory` (Application Loopback mediante NAudio `WithProcessLoopback`), `ProcessLocator`, `AudioApplicationCatalog` (sesiones de audio de Windows agrupadas por aplicación), `ProcessNative` (Toolhelp32 y `QueryFullProcessImageName`) y `ProcessTree` |
| `Sonari.Infrastructure` | `AudioSettings.Sources`: la lista de fuentes se guarda (por ejecutable, nunca por PID) |
| `Sonari.Presentation` | Página **Fuentes** (selector de aplicaciones, audio del sistema y fuentes activas), un **Mezclador** con una tira por fuente, `ShellIcons` (iconos de ejecutables) y `SourceListPersistence` |

## Cómo funciona

### Selector

Lee las **sesiones de audio** de cada salida activa: PID, estado (activa o inactiva) y el nivel de pico que Windows ya mide por sesión. Esto permite ver qué suena **antes** de capturar nada.

Las sesiones se agrupan por **proceso raíz**, subiendo por los padres mientras tengan el mismo ejecutable. Así, Chrome, Discord o Spotify, que reparten el audio entre varios procesos, aparecen como una sola entrada; en tu equipo, Spotify son 8 procesos bajo el PID 4056. Si hay varias instancias, aparecen por separado, con su PID y el título de su ventana.

### Captura

* **Aplicación:** Application Loopback en modo *incluir árbol de procesos* sobre el proceso raíz.
* **Audio del sistema:** el mismo mecanismo en modo *excluir árbol* sobre el PID de Sonari. Así, ni el monitoreo ni los futuros efectos vuelven a entrar.

### Identidad

Una fuente se identifica por la **ruta del ejecutable**. Si la aplicación se cierra, la fuente pasa a «Esperando» y se reengancha sola cuando se vuelve a abrir, aunque sea con otro PID. El sondeo es cada 2 s, y la comprobación de que el proceso sigue vivo, cada 500 ms.

### Independencia entre fuentes

* Añadir o quitar una aplicación no reinicia el micrófono ni la salida.
* Si fallan los auriculares, la captura de las aplicaciones sigue en marcha.
* Cada fuente tiene su propio buffer, su compensación de deriva y sus contadores.

### Envío al monitoreo

Para las aplicaciones y el sistema está **desactivado por defecto**: ya las oyes directamente, y enviarlas otra vez las duplicaría con retardo.

## Resultados en el equipo de desarrollo (23/10/2026, Windows 10 22H2, build 19045)

* **Application Loopback se activa en la build 19045** (la documentación oficial pide la 20348), tanto en modo *incluir* como en modo *excluir*. Entrega paquetes de 10 ms aunque la aplicación esté en silencio, mientras tenga un stream abierto.
* **Prueba completa con la interfaz:**
  1. Fuentes lista Spotify con su icono, sus 8 procesos y el título «Spotify Premium».
  2. Al añadir Spotify y «Audio del sistema» aparece el aviso de duplicación.
  3. Al iniciar el motor, las dos fuentes pasan a «Capturando» sin tocar el micrófono.
  4. La parada es limpia y el registro no tiene advertencias ni errores.
* **Pruebas automáticas:** 93 de 93. Cubren añadir con el motor parado o en marcha, el reenganche al reabrir la aplicación con otro PID, el reintento tras un fallo de captura, que el sistema excluya a Sonari, quitar una fuente sin afectar a las demás, que un fallo de los auriculares no corte las aplicaciones y la agrupación por árbol de procesos.
* **Hallazgo: la captura respeta el volumen de la aplicación.**
  * Con dos procesos de prueba tocando tonos con su volumen de sesión a 0, la captura fue silencio. Con el volumen de stream a 0, también.
  * Si silencias o bajas Spotify en el mezclador de volumen de Windows, en Sonari también llega silenciado o más bajo.
* **Sin verificar todavía:** que solo se captura la aplicación elegida. La prueba sin sonido dio silencio en todas las capturas, incluido el loopback del dispositivo, así que no sirve como control. Hace falta la prueba manual con audio real (abajo).

## Prueba manual que te pido

1. Pon un vídeo de YouTube en el navegador y música en Spotify a la vez.
2. En **Fuentes**, añade solo el navegador.
3. En **Mezclador**, pulsa «Iniciar motor». El medidor del canal del navegador debe moverse con el vídeo.
4. **Pausa el vídeo y deja Spotify sonando:** el medidor del navegador debe quedarse quieto. Si se mueve con la música, se está colando audio de otra aplicación.
5. Opcional: activa «Enviar al monitoreo» en el navegador. Oirás el vídeo dos veces (directo y con un poco de retardo a través de Sonari), pero la música solo una vez.
6. Cierra el navegador con la fuente añadida: el canal pasa a «La aplicación se cerró…». Vuelve a abrirlo y pon un vídeo: en unos segundos pasa a «Capturando (PID nuevo)» y vuelve a sonar.
7. Añade «Audio del sistema»: debe aparecer el aviso de duplicación, y el medidor del sistema debe moverse con YouTube y con Spotify.
8. Cierra Sonari y vuelve a abrirlo: las fuentes añadidas siguen en la lista.

## Limitaciones conocidas

* **Aplicaciones que no se pueden capturar:** las que usan WASAPI en modo exclusivo o ASIO no pasan por el motor de audio compartido de Windows. Aparecen sin actividad.
* **Volumen de la aplicación:** la captura llega con el volumen y el silencio que tenga la aplicación en Windows (ver el hallazgo de arriba).
* **Procesos protegidos o elevados:** si Windows no deja leer la ruta del ejecutable, la aplicación aparece pero no se puede añadir, porque haría falta identificarla por su ruta.
* **Varias instancias:** al reengancharse se elige la instancia preferida si sigue viva; si no, la de PID más bajo. Todavía no se puede elegir entre instancias de forma persistente.
* **Audio del sistema y aplicaciones a la vez:** Windows solo permite excluir **un** árbol de procesos por captura, así que se duplican. Hay aviso, pero no corrección automática.
* **Qué se guarda:** solo la lista de fuentes; ganancias y envíos llegarán con las escenas (Fase 7).
* **Cambio de micrófono o de salida:** sigue reiniciando todo el motor, incluidas las capturas de aplicaciones.
