# Fase 6: efectos de voz

**Objetivo:** que tu micrófono pase por una cadena de efectos configurable, en tiempo real y sin clics al cambiar nada, y que los demás oigan exactamente lo mismo que tú en el monitoreo.

## Cómo funciona

```
Micrófono ──► cadena de voz (una sola vez) ──┬──► mezcla del micrófono virtual (lo que oyen los demás)
                                             └──► mezcla de monitoreo (si activas «Enviar al monitoreo»)
```

* **Dónde se procesa.** En el hilo de captura del micrófono, antes de repartir la voz a las dos mezclas. Se procesa una vez y las dos mezclas reciben la misma voz.
* **Los efectos.** Proyecto nuevo `Sonari.Audio.Effects`, código propio en C# sin dependencias. El limitador de las mezclas (`PeakLimiter`) se movió aquí para que la cadena de voz lo reutilice.
* **Sin reservas de memoria en el audio.** Cada efecto reserva sus buffers al crearse, con el tamaño máximo que pueda necesitar. Con los 10 efectos activados, la cadena no reservó ni un byte procesando 10 s de audio. Gastó entre un 2 y un 2,5 % de un núcleo en el equipo de desarrollo.
* **Cambios en directo.** La configuración es un objeto inmutable (`VoiceEffectSettings`). La interfaz entrega uno nuevo en cada cambio y la cadena lo detecta comparando la referencia, en el bloque siguiente (10 ms).
* **Sin clics.**
  * Encender o apagar un efecto, o el interruptor general, es un fundido de 10 ms entre la voz original y la procesada.
  * Al encenderse, el efecto borra su estado anterior, para que no reaparezca la cola de un eco de hace un rato.
  * Lo que se graba en las líneas de retardo (tono, eco, coro) empieza con una rampa de 10 ms. Así la lectura retrasada nunca llega a un principio de grabación a mitad de onda.
* **Se guarda.** La voz se guarda en `settings.json` 600 ms después del último cambio, para no escribir el archivo en cada movimiento de un deslizador. Se aplica al abrir Sonari, aunque no abras la página «Voz».

## Los efectos

| Efecto | Qué hace | Detalles |
|---|---|---|
| **Puerta de ruido** | Silencia el micrófono cuando no hablas | Ataque de 1 ms, histéresis de 6 dB y 50 ms de retención para no cortar finales de palabra. Cerrada atenúa 80 dB |
| **Filtro** | Pasaaltos (20–500 Hz) y pasabajos (1–20 kHz) | Butterworth de 12 dB/octava. En su máximo, el pasabajos no actúa |
| **Ecualizador** | Graves, medios y agudos (±12 dB) | Estante en 150 Hz, campana en 1,5 kHz y estante en 6 kHz (fórmulas del «Audio EQ Cookbook») |
| **Compresor** | Iguala el volumen | Detección de pico, rodilla suave de 6 dB, ataque y recuperación independientes y ganancia de compensación |
| **Tono** | ±12 semitonos | Línea de retardo con dos lecturas cruzadas. Unos 20 ms de retardo; ver limitaciones |
| **Distorsión** | Saturación tanh + tono | Nivel compensado para −12 dBFS: más saturación no significa más volumen. El pico nunca supera 1 |
| **Modulación** | Robot, coro o flanger | Robot = modulación en anillo (20–200 Hz). El coro abre el estéreo; el flanger lleva realimentación |
| **Eco** | 20–1000 ms, hasta un 90 % de realimentación | Las repeticiones se oscurecen (pasabajos de 5 kHz). Cambiar el tiempo mientras suena desliza el retardo en vez de saltar |
| **Reverberación** | Tamaño, amortiguación y mezcla | Freeverb (dominio público): 8 filtros en peine y 4 pasatodo por canal |
| **Limitador** | Techo de −24 a 0 dB | El mismo limitador con anticipación de las mezclas; añade 1,5 ms |

El **orden** se cambia con las flechas de cada tarjeta, y «Restablecer orden» vuelve al recomendado (el de la tabla).

## Voces predefinidas

| Voz | Qué lleva |
|---|---|
| Sin efectos | Nada |
| Natural | Puerta de ruido, pasaaltos a 80 Hz, compresor suave y limitador |
| Robot | Natural + modulación robot a 55 Hz |
| Ardilla | Natural + tono +7 semitonos |
| Monstruo | Natural + tono −6, graves +4 dB, distorsión y algo de reverberación |
| Radio | Banda de 400 a 3200 Hz, compresión fuerte y distorsión |
| Cueva | Tono −1, eco corto y reverberación grande |
| Eco | Natural + eco de 380 ms |

«Voz actual» indica qué voz predefinida coincide con la configuración, o «Personalizada» si se tocó algo.

## Resultados en el equipo de desarrollo (24/10/2026)

* **Compilación:** 0 advertencias y 0 errores.
* **Pruebas automáticas:** 202 de 202. Las 39 nuevas usan señales sintéticas (senos, impulsos y ruido) y comprueban:
  * **Filtros.** El pasaaltos a 200 Hz atenúa 50 Hz más de 20 dB y deja 2 kHz a ±0,5 dB. El pasabajos a 1 kHz atenúa 8 kHz más de 30 dB. Cada banda del ecualizador da su ganancia con un margen de ±0,7 dB, y plano no cambia nada.
  * **Dinámica.**
    * Puerta de ruido: un seno a −70 dBFS sale atenuado más de 60 dB, y uno a −20 dBFS sale intacto.
    * Compresor: −6 dBFS con umbral −20 y proporción 4:1 da −16,5 dBFS exactos, y la rodilla suave reduce 0,56 dB justo en el umbral.
    * Limitador: ningún pico pasa del techo.
  * **Tono.** ±12, +7 y −5 semitonos dan la frecuencia esperada con un margen del 3 %, sin rastro de la original. 0 semitonos deja pasar la voz idéntica.
  * **Color.** La distorsión genera armónicos sin subir el nivel ni pasar de 1. El robot convierte 1 kHz en 900 y 1100 Hz y no deja nada en 1 kHz. Coro y flanger son estables.
  * **Tiempo.** La primera repetición del eco llega en la muestra exacta y con su nivel. La segunda conserva mezcla × realimentación. Con el 90 % de realimentación el eco se apaga. La reverberación tiene cola y decae, también al 100 %.
  * **Cadena.**
    * La configuración por defecto deja pasar la voz idéntica.
    * Encender y apagar el cambio de tono no produce saltos. Esta prueba destapó un fallo, ya corregido: al apagar un efecto se le aplicaban sus nuevos parámetros en mitad del fundido.
    * El orden cambia el resultado, y todas las voces predefinidas funcionan.
  * **Motor.**
    * El limitador de la voz se aplica a las dos mezclas.
    * Un cambio con el motor en marcha se aplica.
    * Con el interruptor general apagado la voz pasa intacta.
    * La voz sobrevive a un reinicio de la aplicación.

* **Prueba manual:** confirmada el 24/10/2026. Las voces predefinidas y los efectos suenan como se espera.

## Prueba manual que te pido

Con auriculares, para que tus altavoces no se cuelen en el micrófono:

1. **Mezclador:** inicia el motor y activa «Enviar al monitoreo» en el canal Micrófono para oírte.
2. **Voz:** prueba cada voz predefinida hablando. Natural debe sonar limpia, Ardilla aguda, Monstruo grave y áspera, Robot metálica, Radio de teléfono, Cueva con mucha sala y Eco con repeticiones.
3. **Clics:** mientras hablas, enciende y apaga efectos sueltos y el interruptor general. Mueve los deslizadores, sobre todo el tiempo del eco y los semitonos. No debe oírse ningún clic ni chasquido. Lo único que cambia de golpe es el sonido del efecto, que es lo esperado.
4. **Puerta de ruido:** enciéndela y calla. El ruido de fondo debe desaparecer y tus palabras no deben cortarse al principio ni al final. Si se cortan, baja el umbral.
5. **Orden:** pon el eco después de la reverberación y luego antes, y escucha la diferencia.
6. **Que se guarda:** cierra Sonari, vuelve a abrirlo y comprueba que la voz sigue igual.
7. **Lo que oyen los demás:** con un cable virtual (VB-CABLE o el driver de Sonari en la VM), graba el micrófono virtual con la Grabadora de voz. Debe sonar igual que en tu monitoreo.

## Limitaciones conocidas

* **Cambio de tono sencillo.** El método de dos lecturas cruzadas es barato y de baja latencia, pero deja una ligera aspereza: la energía se reparte en bandas separadas 25 Hz alrededor del tono, en vez de quedarse en un tono puro. Tampoco conserva los formantes, de ahí la voz de ardilla o de monstruo. Para una voz más grave o más aguda pero natural haría falta PSOLA o un vocoder de fase; se puede valorar más adelante.
* **Latencia añadida.** El cambio de tono añade unos 20 ms y el limitador 1,5 ms; el resto de efectos, nada. Se medirá de punta a punta en la Fase 8.
* **Reverberación al 100 %.** Con el tamaño al máximo la cola dura muchos segundos y puede sonar muy fuerte. Los limitadores de las mezclas evitan que sature la salida.
* **Ecualizador de frecuencias fijas.** Las tres bandas tienen la frecuencia fija; solo se ajusta la ganancia.
* **Una sola configuración.** Guardar varias voces propias con nombre y cambiarlas con atajos llegará con las escenas (Fase 7).
