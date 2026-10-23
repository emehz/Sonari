# Fase 4: mezclador completo y reproductor de sonidos

**Objetivo:** que el mezclador controle de verdad la señal (balance, limitador sin saturación, silencio global) y añadir el panel de sonidos, que entra en la misma cadena de mezcla que el resto de fuentes.

## Qué se añadió

| Proyecto | Contenido |
|---|---|
| `Sonari.Audio.Core` | `SoundClip`, `ISoundPlayer`, `ISoundDecoder`, `ISoundClipProvider` y `SourceKind.SoundPlayer`. `IChannelControl.Balance`, `IAudioEngine.IsGlobalMuted`, `IAudioEngine.Sounds`/`SoundChannel`/`Format` y la reducción del limitador en `EngineMetrics` |
| `Sonari.Audio.Engine` | `PeakLimiter` (con lookahead), `StereoGain` (fader × balance con rampa), `ChannelStrip` (procesado común de un canal) con dos variantes: `MixerChannel` (fuentes capturadas) y `GeneratorChannel` (generadas en el motor). Además, `SoundPlayer` (32 voces) y `SoundClipCache` |
| `Sonari.Audio.Playback` *(nuevo)* | `MediaFoundationSoundDecoder`: WAV, MP3, AAC/M4A, WMA y FLAC, convertidos a 48 kHz estéreo. Es el único proyecto de reproducción que usa NAudio |
| `Sonari.Infrastructure` | `SoundboardLibrary`, `SoundBank`, `SoundEntry` y `SoundboardStore` (archivo `%APPDATA%\Sonari\soundboard.json`) |
| `Sonari.Presentation` | Página **Sonidos**, balance y «Silencio global» en el Mezclador, lectura del limitador y estilo de TextBox |

## Decisiones técnicas

### Limitador del bus de monitoreo

* **Ajustes:** techo de −0,3 dBFS, 1,5 ms de lookahead (64 frames a 48 kHz) y release de 100 ms.
* **Algoritmo:** mínimo móvil de la ganancia necesaria, luego un release suave y luego una media móvil.
  * Como la media de cada frame solo usa valores que ya «vieron» ese frame, **ningún frame puede superar el techo**, y la ganancia cambia sin saltos.
  * Comprobado con señales de +6 a +12 dB y con picos de una sola muestra.
* **Protección final:** tras el limitador sigue el recorte de seguridad, cuyo contador debería quedarse en 0.

### Balance

Es una ley de balance, no de panorama: en el centro la señal no se altera, y al girar solo se atenúa el lado contrario. Va combinado con el fader en una sola rampa por bloque. Doble clic sobre el slider lo devuelve al centro.

### Silencio global

Corta la salida del bus con un fundido de un bloque. Las fuentes siguen capturando, así que al quitarlo el audio vuelve al instante. En la Fase 5 también cortará el micrófono virtual.

### Reproductor de sonidos

* **Dónde suena:** genera el audio dentro del hilo de render. No hay captura ni deriva que compensar, así que no necesita ring buffer.
* **Voces:** 32 preasignadas, sin asignar memoria al reproducir. Si se llenan, se reutiliza la más antigua.
* **Órdenes:** reproducir, parar, pausar, reanudar, volumen y bucle van por una cola sin bloqueos y se aplican en el siguiente bloque (≈ 10 ms).
* **Sin chasquidos:** empezar, parar y pausar usan fundidos de 5–10 ms. Reproducir un sonido que ya suena lo reinicia con un fundido cruzado exacto.
* **Estado para la UI:** posición y estado se publican con un seqlock, así la UI lee sin bloquear el audio.
* **Parada de emergencia:** descarta las órdenes pendientes y funde todo en 5 ms.

### Decodificación

* Los sonidos se decodifican **una vez** en segundo plano al abrir el banco y quedan en memoria (presupuesto de 512 MB; se descartan los menos usados).
* Pulsar «reproducir» solo copia muestras.
* Límite de 5 minutos por sonido (≈ 110 MB en float32).

### Canal «Sonidos»

* Es fijo: no se puede quitar del mezclador.
* Su envío al monitoreo está **activado por defecto**, porque los sonidos no tienen otra vía para llegar a tus oídos. En el micrófono y las aplicaciones sigue desactivado.

## Resultados en el equipo de desarrollo (23/10/2026)

* **Compilación:** 0 advertencias y 0 errores.
* **Pruebas automáticas:** 126 de 126. Cubren:
  * El limitador: techo, transparencia por debajo del techo, picos aislados y recuperación.
  * La ley de balance y su rampa.
  * El reproductor: fundidos, final de un sonido, bucle, parada de emergencia, pausa y reanudación desde la misma posición, reinicio sin solaparse, clips en otro formato, reutilización de voces y volumen en vivo.
  * La decodificación real de un WAV mono a 44,1 kHz a estéreo a 48 kHz, conservando la frecuencia.
  * La caché, la biblioteca de sonidos, el silencio global, y el limitador del motor con micrófono + sonido a 0,9 cada uno (pico de salida ≤ −0,3 dBFS y 0 muestras recortadas).
* **Prueba con la interfaz, usando un WAV de silencio para no hacer ruido:**
  * El sonido se reproduce, el progreso avanza (0:00/0:03) y aparece «1 sonando».
  * Un archivo inexistente muestra su aviso.
  * El Mezclador muestra el balance, «Silencio global» y el canal «Sonidos» en estado «Listo».
  * El registro no tiene advertencias ni errores.

## Prueba manual que te pido

1. En el **Mezclador**, pulsa «Iniciar motor».
2. En **Sonidos**, pulsa «Importar sonidos» y elige varios archivos: un MP3, un WAV y, si tienes, un M4A o un FLAC.
3. Reproduce varios a la vez y comprueba que suenan juntos sin chasquidos al empezar ni al parar.
4. Pausa uno y reanúdalo: debe seguir desde donde estaba. Pulsa reproducir otra vez sobre uno que suena: debe reiniciarse sin solaparse.
5. Activa «Bucle» en uno y comprueba que se repite sin hueco ni chasquido.
6. Pulsa **«Detener todo»**: todo debe callarse al instante.
7. Prueba las categorías (escribe una en un sonido y filtra), crea un segundo banco y reordena con ↑↓.
8. En el Mezclador, mueve el balance del canal «Sonidos» y pulsa **«Silencio global»**: no debe salir nada hasta que lo quites.
9. Cierra Sonari y vuelve a abrirlo: bancos, sonidos, volúmenes y bucles siguen ahí.

## Limitaciones conocidas

* **Atajos de teclado:** los globales para lanzar sonidos y para la parada de emergencia llegan en la Fase 7.
* **Envío al micrófono virtual:** los sonidos solo van al monitoreo por ahora. Llegará en la Fase 5, con su propio control de envío por canal.
* **Archivos referenciados:** los sonidos se guardan por ruta. Si mueves o borras el archivo, aparece «No se encuentra el archivo».
* **Formatos:** solo los que decodifica Media Foundation en tu Windows (OGG y OPUS, por ejemplo, no vienen de serie).
* **Dependen del motor:** si la salida de monitoreo falla, los sonidos se detienen hasta que se recupere, porque se generan al ritmo de esa salida.
* **Cambiar la frecuencia del motor** en Ajustes obliga a volver a decodificar los sonidos; se hace solo la primera vez que se reproducen.
