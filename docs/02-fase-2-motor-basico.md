# Fase 2: motor de audio básico

**Objetivo:** que el audio pase del micrófono físico a los auriculares de forma estable, con medidores, contadores de diagnóstico y recuperación cuando un dispositivo se desconecta.

## Qué se añadió

| Proyecto | Contenido |
|---|---|
| `OndaMix.Audio.Core` | Contratos `IAudioStream`, `ICaptureStreamFactory`, `IRenderStreamFactory`, `IAudioDeviceWatcher` e `IAudioEngine`, más los tipos de estado y métricas |
| `OndaMix.Audio.Engine` *(nuevo; sustituye al «Audio.Mixing» del diseño)* | `DriftCompensatingReader`, `MixerChannel`, `MonitorBus`, `SmoothedGain`, `PeakMeter`, `SafetyClipper` y el supervisor `AudioEngine`. No usa NAudio, así que se prueba con dispositivos simulados |
| `OndaMix.Audio.Capture` *(nuevo)* | `WasapiCaptureStreamFactory`: micrófono en modo compartido, por eventos, con MMCSS «Pro Audio» |
| `OndaMix.Audio.Output` *(nuevo)* | `WasapiRenderStreamFactory`: salida en modo compartido, por eventos, con MMCSS |
| `OndaMix.Audio.Devices` | `WasapiDeviceWatcher` (avisos de conexión y desconexión) y `WasapiEndpointResolver` |
| `OndaMix.Presentation` | Página **Mezclador** (canal de micrófono, bus de monitoreo y diagnóstico), control `LevelMeter` y estilos de Slider e interruptor |
| `OndaMix.App` | Registro del motor y `AudioEngineLifetime`, que libera los dispositivos al cerrar la aplicación |
| raíz | `global.json`: `dotnet test` usa Microsoft.Testing.Platform, obligatorio con xUnit v3 en el SDK de .NET 10 |

## Decisiones técnicas

### Formato

Se piden a WASAPI directamente float32, 48 kHz y estéreo, tanto en la captura como en la salida. El motor de audio de Windows se encarga de convertir frecuencia, canales y profundidad de bits. Se comprobó con tu hardware: tus auriculares G733 mezclan internamente a 44,1 kHz con 8 canales y aceptaron 48 kHz estéreo sin errores.

### Deriva de reloj

El micrófono y los auriculares tienen relojes independientes; en tu equipo, la diferencia medida fue de entre +43 y +69 ppm. `DriftCompensatingReader` funciona así:

* **Remuestreo:** interpolación cúbica Catmull-Rom.
* **Control de la velocidad de lectura:** un regulador proporcional sobre el nivel medio del buffer.
  * Ganancia de 0,1 por segundo de error, con suavizado de unos 0,5 s.
  * Corrección limitada a ±1000 ppm (alrededor de 1,7 centésimas de semitono, inaudible).
* **Estados:**
  * **Cebado:** devuelve silencio hasta que el buffer alcanza el objetivo.
  * **Underrun:** se cuenta y el lector vuelve al cebado.
  * **Exceso de más de 4× el objetivo:** se descarta de golpe (*resync*).

### Hilos

* Hilo de captura (NAudio, MMCSS) → `SpscRingBuffer` sin bloqueos → hilo de render (NAudio, MMCSS).
* Ninguno de los dos asigna memoria ni bloquea.
* La UI lee las métricas mediante un struct a 30 Hz.

### Recuperación

El supervisor reabre los dos streams en estos casos:

* Uno de los dos se detiene con error.
* Windows avisa de un cambio de dispositivo.
* Cambia el dispositivo predeterminado (solo si el usuario dejó «Predeterminado de Windows»).

Agrupa las ráfagas de avisos en ventanas de 300 ms y, si sigue fallando, reintenta cada 2 s.

### Seguridad contra acoples

El envío del micrófono al monitoreo arranca **desactivado** en cada inicio y solo se activa con el interruptor.

### GC

Mientras el motor funciona, se usa `GCLatencyMode.SustainedLowLatency`.

## Presupuesto de latencia micrófono → auriculares (estimado, no medido)

| Tramo | ms |
|---|---|
| Periodo de captura de WASAPI | ≈ 10 |
| Buffer de compensación (objetivo) | 15 |
| Mitad de un bloque de render | ≈ 5 |
| Buffer de salida (`WithLatency`) | 20 |
| **Total aproximado** | **≈ 50** |

La medición real, con un bucle y un clic de prueba, está prevista para la Fase 8. Con un bloque de 5 ms en Ajustes el total baja a unos 32 ms, con más riesgo de cortes.

## Resultados en el equipo de desarrollo (22/10/2026)

* La solución compila con el SDK 10.0.402 con 0 advertencias y 0 errores.
* Pasan las 76 pruebas automáticas, entre ellas una simulación de 10 minutos con ±300 ppm de deriva sin ningún underrun y la recuperación ante fallos con dispositivos simulados.
* Prueba real durante 80 s con Leviosa Max → G733:
  * Buffer estable en 15,4–15,7 ms y corrección de deriva de +43 a +69 ppm.
  * 0 underruns, 0 overruns, 0 resyncs y 0 muestras recortadas.
  * 1 hueco de captura, el aviso habitual del primer paquete al arrancar.
* La parada es limpia: «Motor detenido» y la app se cierra con código 0.

## Prueba manual que te pido

1. Selecciona **Mezclador** → **Iniciar motor**.
2. Con **auriculares puestos**, activa «Escucharme por los auriculares» y habla. Deberías oírte con un pequeño retardo y ver moverse los dos medidores.
3. Déjalo funcionar 10 minutos. «Vaciados» y «Muestras perdidas» deberían seguir en 0 o casi.
4. Desconecta los auriculares (o el micro USB) con el motor en marcha. El estado debe pasar a amarillo («Reintentando…») sin que la app se cierre.
5. Vuelve a conectarlo. En unos segundos el estado debe volver a verde y el audio debe regresar.
6. Con «Predeterminado de Windows» seleccionado, cambia la salida predeterminada en la configuración de sonido de Windows. El motor debe reabrir el nuevo dispositivo.
7. Mueve los sliders y usa los botones de silencio: no debería oírse ningún clic.

## Limitaciones conocidas

* Cambiar de dispositivo, o recuperarse de un fallo, reinicia **los dos** streams: hay un corte breve aunque solo haya fallado uno. Se refinará cuando haya más fuentes (Fase 4).
* No se usa IAudioClient3 (baja latencia), porque es incompatible con la conversión de formato automática. Se evaluará en la Fase 8 para dispositivos que ya trabajen a 48 kHz.
* La latencia de la tabla es una estimación. Los «ms» que muestra la app son tamaños de buffer, no la latencia total medida.
* El medidor post-fader del micrófono se calcula, pero aún no se muestra.
* Ni la ganancia ni el estado de los envíos se guardan: llegarán con las escenas (Fase 7).
* Si monitorizas por altavoces, el micrófono puede acoplarse. La app lo advierte, pero no lo impide.
