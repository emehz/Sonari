# Fase 5: micrófono virtual

**Objetivo:** que la mezcla llegue a un micrófono que Discord, juegos y cualquier otra aplicación puedan elegir, sin que tu monitoreo se cuele en él.

## Cómo funciona

Windows solo crea micrófonos mediante drivers de audio (ver el documento 01, sección C). Mientras Sonari no tenga el suyo, se usa un **cable virtual**:

```
Sonari (mezcla del micrófono virtual) ──► «CABLE Input»  ═══ VB-CABLE ═══►  «CABLE Output» ──► Discord, juegos, OBS…
Sonari (mezcla de monitoreo)          ──► tus auriculares
```

* **Dos mezclas independientes:** cada fuente tiene dos envíos, «Enviar al micrófono virtual» y «Enviar al monitoreo».
  * Por defecto, todo va al micrófono virtual.
  * Al monitoreo van solo los sonidos; el micrófono y las aplicaciones, solo si lo activas.
  * Cada mezcla tiene su volumen, su silencio, su medidor y su limitador (−0,3 dBFS).
* **Cada mezcla con su propio reloj:** cada fuente reparte su audio a un buffer por mezcla («feed»), y cada mezcla lo lee compensando la deriva respecto a su dispositivo. El cable y los auriculares nunca van exactamente a la misma velocidad.
* **Sonidos:** se generan al ritmo del micrófono virtual cuando está activo. Así, si los auriculares se desconectan, los demás siguen oyendo los sonidos.
* **El monitoreo nunca entra en el micrófono virtual:** son dos mezclas separadas por estructura.
* **El silencio global corta las dos.**

### Protecciones contra la realimentación

| Situación | Qué hace Sonari |
|---|---|
| Tu micrófono es la salida del propio cable («CABLE Output») | **Error.** El micrófono virtual queda desactivado, porque sería un bucle infinito |
| Monitoreo y micrófono virtual en el mismo dispositivo | **Error.** El motor rechaza la salida virtual y los demás no oyen tu monitoreo |
| Windows tiene el cable como salida predeterminada | **Aviso:** todo el audio del PC acabaría en el micrófono |
| La salida elegida no parece un cable | **Aviso:** la mezcla sonaría por ese dispositivo |
| El cable no existe o falla | El motor **sigue funcionando** con micrófono y auriculares, avisa y reintenta cada 2 s **sin cortar el monitoreo** |

## Instalar VB-CABLE (una vez por PC)

1. Descarga **VB-CABLE Driver Pack** desde https://vb-audio.com/Cable/. Es *donationware*: gratis para uso personal, y se agradece una donación si te resulta útil.
2. Descomprime el ZIP.
3. Haz clic derecho en `VBCABLE_Setup_x64.exe` → **Ejecutar como administrador** → **Install Driver**.
4. **Reinicia Windows.**
5. Comprueba en **Configuración → Sistema → Sonido** que tus auriculares siguen siendo la **salida predeterminada**. A veces Windows elige el cable recién instalado, y entonces todo tu audio iría al micrófono.

Cada amigo tiene que instalarlo en su propio PC. Sonari no puede incluirlo en su instalador sin un acuerdo con VB-Audio.

## Configurar Sonari y Discord

1. En Sonari → **Mezclador**:
   * **Micrófono:** tu micro real.
   * **Monitoreo:** tus auriculares.
   * **Micrófono virtual:** «CABLE Input (VB-Audio Virtual Cable)».
2. Pulsa **Iniciar motor**. La tarjeta «Micrófono virtual» debe decir «Enviando a «CABLE Input…»».
3. En Discord → **Ajustes de usuario → Voz y vídeo → Dispositivo de entrada** → «CABLE Output (VB-Audio Virtual Cable)». Sonari muestra este nombre exacto debajo del selector.
4. Recomendado en Discord: desactiva la **supresión de ruido** (Krisp) y la **cancelación de eco**. Están pensadas para un micrófono real y pueden recortar la música y los sonidos que les mandas.

## Resultados en el equipo de desarrollo (23/10/2026)

* **Compilación:** 0 advertencias y 0 errores.
* **Pruebas automáticas:** 142 de 142. Las 11 nuevas del micrófono virtual comprueban:
  * El micrófono va al virtual y no al monitoreo, y los envíos funcionan por separado.
  * El volumen y el silencio del virtual no tocan el monitoreo, y el silencio global corta las dos mezclas.
  * Los sonidos llegan a las dos mezclas, y siguen llegando al virtual aunque los auriculares dejen de pedir audio.
  * Un cable ausente no detiene el motor y se recupera solo sin reabrir el micrófono; un fallo del cable se recupera sin tocar los auriculares.
  * Monitoreo y virtual en el mismo dispositivo se rechazan, y sin cable no se abre un tercer stream.
  * También se prueba el emparejamiento de los extremos del cable, incluido un nombre con paréntesis anidados que destapó un fallo, ya corregido.
* **Prueba real con tres streams a la vez:** micrófono Leviosa Max, monitoreo G733 y los altavoces Realtek haciendo de micrófono virtual, con el silencio global y el del virtual activados para no emitir sonido.
  * El estado mostró «Enviando a «Altavoces (Realtek…)»» junto con el aviso «no parece un cable virtual».
  * El registro no tiene advertencias ni errores.
* **No verificado todavía:** que Discord reciba la mezcla. En este PC no hay VB-CABLE instalado. Es la prueba manual de abajo.

## Prueba manual que te pido (con VB-CABLE instalado)

1. Configura Sonari como arriba e inicia el motor.
2. **Sin Discord:** abre la **Grabadora de sonido** de Windows, elige «CABLE Output» como micrófono y graba 10 segundos hablando, con un sonido del panel y una aplicación añadida sonando. Al reproducir la grabación deben oírse las tres cosas.
3. Activa «Enviar al monitoreo» en el micrófono y repite la grabación: **no debe cambiar nada** en la grabación, porque el monitoreo no entra en el micrófono virtual.
4. **Con Discord:** únete a un canal de voz con un amigo, o usa el botón «Prueba de micrófono» de Ajustes → Voz y vídeo. Debe oírse tu voz, los sonidos y la aplicación.
5. Pulsa «Silencio global»: el indicador de voz de Discord debe apagarse.
6. Elige «CABLE Output» como micrófono en Sonari: debe aparecer el error de realimentación y el micrófono virtual debe quedar desactivado. Vuelve a tu micrófono real.

## Limitaciones conocidas

* **Dependencia de VB-CABLE u otro cable:** el micrófono propio «Sonari Virtual Microphone» requiere un driver en C++ y firmarlo con un certificado EV (documento 01, sección C). El driver ya funciona en pruebas (`driver/`, ver su README), pero todavía no se distribuye.
  * Si está instalado, Sonari lo reconoce por el nombre del dispositivo, «Sonari Virtual Audio». Lo pone el primero de la lista y lo elige solo para el micrófono virtual si el usuario no eligió otra cosa, o si lo que eligió ya no existe.
  * Las protecciones contra la realimentación y la indicación para Discord funcionan igual que con VB-CABLE.
* **Nombre del micrófono:** con VB-CABLE, el micrófono se llama «CABLE Output». Puedes renombrarlo en Windows (Panel de control de sonido → Grabación → Propiedades), pero ese nombre lo controla el driver.
* **Latencia:** el cable añade su propio buffer, configurable en el panel de VB-CABLE, y Discord el suyo. La latencia total se medirá en la Fase 8.
* **Cambiar el micrófono o el monitoreo** sigue reabriendo todo el motor (corte breve). Cambiar o perder **solo** el cable no corta nada más.
* **Qué se guarda:** los envíos por canal todavía no se guardan; llegarán con las escenas (Fase 7). Cada vez que abres Sonari vuelven a los valores por defecto: todo al micrófono virtual y solo los sonidos al monitoreo.
