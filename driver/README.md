# Driver de audio de Sonari

Driver de audio virtual en modo kernel (WaveRT/PortCls) que acabará siendo el **«Sonari Virtual Microphone»**: Sonari escribirá su mezcla en un altavoz virtual y Discord, los juegos u OBS la leerán como si fuera un micrófono, sin depender de VB-CABLE.

> **Estado: etapa D4 superada.** Funciona como un cable virtual: lo que cualquier aplicación reproduce en «Sonari Virtual Output» lo graba quien abra «Sonari Virtual Microphone», con 30 ms de retardo. Pasó Driver Verifier y las pruebas de retirada en caliente. Sigue siendo solo para la máquina virtual hasta que tenga instalador y firma (D6).

## Origen y licencia

* **Base:** [SimpleAudioSample](https://github.com/microsoft/Windows-driver-samples/tree/main/audio/simpleaudiosample) de los ejemplos oficiales de drivers de Microsoft, commit `bb68ed5e770bce04dc5395ec9258dafab034184d` (20/10/2026).
* **Licencia:** Microsoft Public License (MS-PL), copiada en [LICENSE](LICENSE). Esta licencia cubre todo lo que hay en `driver/`, incluidos nuestros cambios. El resto de Sonari tiene su propia licencia.
* **README original de Microsoft:** [README.upstream.md](README.upstream.md).

## Cambios respecto al original

| Etapa | Archivo | Cambio | Por qué |
|---|---|---|---|
| D1 | `Source/Main/SimpleAudioSample.inx` | `NT$ARCH$.10.0...22000` → `NT$ARCH$.10.0...19041` | El original solo se instala en Windows 11. Sonari necesita Windows 10 2004 o posterior |
| D1 | `Directory.Build.props` (nuevo) | `_NT_TARGET_VERSION` = `0xA000008` (Windows 10 2004) | Compila contra las cabeceras de Windows 10 2004: usar una API exclusiva de Windows 11 da error de compilación. Además corta la herencia del `Directory.Build.props` de .NET de la raíz |
| D1 | `build.ps1`, `vm/` (nuevos) | Compilación y scripts de instalación | Dejan en `out/` todo lo que hay que copiar a la máquina virtual |
| D2 | Solución, INF, recursos y binario | `SimpleAudioSample.*` → `SonariAudio.*`, servicio `SonariAudio`, hardware ID `ROOT\SonariVirtualAudio` | Identidad propia: no choca con el ejemplo de Microsoft si alguien también lo tiene instalado |
| D2 | `SonariAudio.inx` | Proveedor y fabricante «Sonari», dispositivo «Sonari Virtual Audio», nombres de los filtros KS | Sustituye los «TODO» y los nombres del ejemplo |
| D2 | `SonariAudio.inx`, `micarray1toptable.h`, `speakertoptable.h` | GUID propios en el campo *Name* de los pines puente y su texto en `MediaCategories` | Windows toma de ahí el nombre del endpoint: «Sonari Virtual Microphone» y «Sonari Virtual Output». Es el mismo mecanismo que usa SYSVAD |
| D2 | `speakertoptable.h` | Categoría de la salida `KSNODETYPE_SPEAKER` → `KSNODETYPE_LINE_CONNECTOR` (salida de línea) | Con la categoría «altavoces», Windows llama al endpoint «Altavoces» e ignora el nombre propio (comprobado en la VM; el micrófono sí mostraba su nombre) |
| D2 | `micarray1toptable.h` | Categoría `KSNODETYPE_MICROPHONE_ARRAY` → `KSNODETYPE_MICROPHONE`, sin la propiedad de geometría del conjunto | Que Windows lo trate como un micrófono normal y no le aplique procesado de *array* |
| D2 | `definitions.h`, `endpoints.h` | GUID de producto y de nombre propios; prefijo de depuración `SONARI:` | No reutilizar identificadores de Microsoft |
| D2 | `SonariAudio.rc` | Información de versión propia (Sonari 0.1.0.0), con la atribución a Microsoft | Antes decía «Microsoft Virtual Simple Audio Sample Driver» |
| D3 | `Utilities/LoopbackBuffer.*` (nuevos) | Buffer circular de 1 s entre la salida y el micrófono | Es el «cable» en sí. Ver [Cómo funciona el loopback](#cómo-funciona-el-loopback-d3) |
| D3 | `common.h`, `common.cpp` | El adaptador crea y libera el buffer y lo ofrece con `GetLoopback()` | Hay un solo buffer por dispositivo, compartido por los dos streams |
| D3 | `minwavertstream.*` | La salida copia lo que reproduce al buffer (`WriteLoopback`); el micrófono lee de él (`WriteBytes`) en lugar de generar el tono | Conecta los dos endpoints. El generador de tono (`ToneGenerator.*`) queda en el proyecto, sin usar |
| D4 | `speakertoptable.h`, `micarray1toptable.h` | Sin nodos de volumen ni de silencio en las topologías | Así Windows aplica el volumen y el silencio por software ([documentación](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/software-volume-control-support)). Antes los controles de Windows no hacían nada |
| D4 | `adapter.cpp`, `LoopbackBuffer.*` | Retardo configurable con el valor de registro `LoopbackDelayMs` (5–500 ms, 30 por defecto) | Ajustarlo en cada equipo sin recompilar |
| D4 | `LoopbackBuffer.*`, `minwavertstream.*` | Contador de «audio tardío» y mensaje de depuración al parar la captura | Medir si el retardo es suficiente (ver [Pruebas de D4](#pruebas-de-d4)) |

Los identificadores internos del código C++ (clases `CMiniportTopologySimpleAudioSample`, secciones `SIMPLEAUDIOSAMPLE_*` del INF, `WaveMicArray1`…) se mantienen a propósito: no se ven fuera del driver y así es fácil comparar con el original de Microsoft.

## Requisitos para compilar

* Visual Studio 2026 con «Desarrollo para el escritorio con C++», las bibliotecas Spectre (x64/x86) y el componente «Windows Driver Kit».
* Windows SDK y WDK **10.0.28000** (el mismo número en los dos).

## Compilar

Desde PowerShell, en esta carpeta:

```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

También puedes abrir `SonariAudio.sln` en Visual Studio, elegir **Debug | x64** y compilar. En ese caso, `out/` no se genera.

El resultado queda en `out/` (ignorada por git):

| Archivo | Qué es |
|---|---|
| `SonariAudio.inf` / `.sys` / `.cat` | El paquete del driver, firmado con un certificado de prueba |
| `SonariTest.cer` | El certificado de prueba con el que se firmó. Lo genera Visual Studio en tu PC; no se sube al repositorio |
| `devcon.exe` | Herramienta del WDK que crea el dispositivo virtual |
| `install.cmd` / `uninstall.cmd` | Instalan y quitan el driver en la máquina virtual |

## Instalar en la máquina virtual de pruebas

**Nunca en tu PC principal.** Un fallo en un driver de kernel produce una pantalla azul.

Requisitos de la VM:
* Arranque seguro desactivado.
* `bcdedit /set testsigning on` y reinicio. Debe verse «Modo de prueba» en el escritorio.
* Una instantánea reciente.

Pasos:
1. Toma una instantánea.
2. Copia la carpeta `out` desde la carpeta compartida a un disco local de la VM, por ejemplo `C:\SonariDriver`. No la ejecutes desde la carpeta compartida: devcon y el instalador de drivers pueden fallar con rutas de red.
3. Clic derecho en `install.cmd` → **Ejecutar como administrador**. El script:
   * Añade `SonariTest.cer` a «Entidades de certificación raíz de confianza» y a «Editores de confianza» de la VM.
   * Crea el dispositivo `Root\SonariVirtualAudio`. Si ya existe, actualiza el driver.

Para quitarlo, ejecuta `uninstall.cmd` como administrador, o mejor, restaura la instantánea.

## Cómo funciona el loopback (D3)

* **Un reloj común.** El ejemplo de Microsoft no tiene hardware: cada stream avanza su posición según el tiempo del contador de rendimiento de Windows (QPC). El buffer numera cada muestra con ese mismo reloj (48 000 por segundo), así que la salida y el micrófono van exactamente a la misma velocidad y no hay deriva que compensar.
* **La salida escribe** cada bloque que «reproduce» en la posición que le corresponde según ese reloj.
* **El micrófono lee 30 ms por detrás** (configurable, ver abajo). La salida actualiza su posición cada 10 ms aproximadamente, así que el micrófono espera a que esos datos estén escritos. El margen cubre los retrasos del temporizador, mayores en máquinas virtuales.
* **Silencio cuando no hay nada.** Si nadie reproduce, la salida está en pausa o llega tarde, el micrófono recibe silencio, nunca audio antiguo: los huecos se rellenan con ceros y lo que tiene más de 1 s se considera sobrescrito.
* **Formatos.** La salida trabaja a 48 kHz, estéreo, 16 bits; el micrófono a 48 kHz, estéreo, 32 bits. El buffer guarda enteros de 32 bits y convierte. Windows convierte lo que mandan las aplicaciones al formato de la salida.
* **Contenido protegido (DRM).** Si Windows marca lo que se reproduce como protegido contra copia, la salida escribe silencio. El INF declara el driver como compatible con DRM, así que está obligado a no dejarlo grabar.
* **Concurrencia.** Las dos partes se ejecutan en temporizadores del kernel (IRQL `DISPATCH_LEVEL`), posiblemente en procesadores distintos. El buffer está en memoria no paginable y protegido con un spinlock; cada acceso copia como mucho unos pocos milisegundos de audio.

### Limitaciones conocidas

* **16 bits en la salida.** Suficiente para voz y para Discord, que comprime con Opus. Se puede subir a 24 o 32 bits más adelante.
* **Un solo stream por endpoint.** Es lo normal en modo compartido (Windows mezcla todas las aplicaciones en uno). En modo exclusivo, solo una aplicación a la vez.

### Ajustar el retardo

El valor `LoopbackDelayMs` (DWORD, en milisegundos) se lee al cargar el driver:

```
reg add HKLM\SYSTEM\CurrentControlSet\Services\SonariAudio\Parameters /v LoopbackDelayMs /t REG_DWORD /d 20 /f
```

Después hay que recargar el driver: deshabilitar y volver a habilitar «Sonari Virtual Audio» en el Administrador de dispositivos, o reiniciar. Menos retardo = menos latencia, pero si la salida llega tarde se oyen cortes; el contador de audio tardío lo detecta.

## Pruebas de D4

Siempre en la máquina virtual, partiendo de la instantánea «Base de pruebas».

1. **Volumen y silencio.** Con «Escuchar este dispositivo» activado en el micrófono y música sonando por «Sonari Virtual Output»: bajar el volumen o silenciar la salida (Propiedades → Niveles) y luego el micrófono debe bajar o silenciar lo que se oye.
2. **Driver Verifier.** `verifier /standard /driver SonariAudio.sys` como administrador y reiniciar (`verifier /querysettings` para comprobarlo). Con él activo: 10 minutos de música con pausas, deshabilitar y habilitar el dispositivo varias veces mientras suena, y `uninstall.cmd` / `install.cmd`. Al acabar, `verifier /reset` y reiniciar. Si hay pantalla azul, copiar `C:\Windows\MEMORY.DMP` antes de restaurar la instantánea.
3. **Audio tardío y retardo.** Con [DebugView](https://learn.microsoft.com/en-us/sysinternals/downloads/debugview) (Sysinternals, de Microsoft) como administrador y *Capture Kernel* + *Enable Verbose Kernel Output* activados, cada vez que se para una captura aparece `SONARI: Capture stopped: N ms captured, M ms of late audio`. Repetir con `LoopbackDelayMs` = 30, 20 y 10.

Los mensajes de depuración solo existen en la compilación Debug, que es la que genera `build.ps1` por defecto.

## Hallazgos de las pruebas

* **Modo de prueba:** si Windows no arrancó en modo de prueba, el dispositivo aparece con el código 52 (firma no comprobable). `install.cmd` lo comprueba antes de instalar.
* **Deshabilitar pide reiniciar.** Windows anota el motivo en el Visor de eventos (Sistema, Kernel-PnP, id. 225): `audiodg.exe`, el motor de audio de Windows, tiene abiertos los endpoints y bloquea la retirada en caliente. Ocurre con «Sonari Virtual Output» como salida predeterminada, o con el Panel de Sonido o una aplicación usándolo. El driver no puede cerrar esas conexiones; al reiniciar se aplica el cambio sin problemas. Lo mismo pasa con VB-CABLE, que pide reiniciar al instalar y desinstalar. Habrá que tenerlo en cuenta en el instalador (D6).
* **Pantalla azul al instalar con un dispositivo pendiente de quitar (corregido).** El driver admite un solo dispositivo a la vez, así que el segundo no arranca (`STATUS_DEVICE_BUSY`, código 10). El ejemplo de Microsoft lo notifica con un mensaje de nivel error, y en la compilación Debug, `_DbgPrintF` (`ksdebug.h`) llama a `DbgBreakPoint()` tras cualquier mensaje de error. Sin un depurador conectado, eso es una pantalla azul `0x7E` (`STATUS_BREAKPOINT`), que VirtualBox no llegó a mostrar: parecía un cuelgue. Lo encontró el volcado de la VM: `SonariAudio!StartDevice` → `DbgPrint` → `int 3`. Ahora `D_ERROR` imprime con nivel de aviso y no detiene el sistema (0 de 116 mensajes lo hacen). Además, `install.cmd` ya no instala si hay un dispositivo pendiente de quitar, y `uninstall.cmd` avisa de que hay que reiniciar.
* **Pantalla azul al desinstalar con música sonando (corregido).** Código `0xD1` en `TimerNotifyRT` (`minwavertstream.cpp`). Al quitar el dispositivo, `PnpHandler` libera el adaptador en cuanto llega la retirada, pero `audiodg.exe` todavía tiene el stream abierto y su temporizador sigue usando el adaptador (y su buffer de loopback) ya liberado. El miniport de onda del ejemplo de Microsoft guardaba una referencia débil al adaptador. Ahora guarda una fuerte, como ya hacían los miniports de topología, y el adaptador vive hasta que se cierra el último stream. Además, el destructor del stream detiene su temporizador **antes** de liberar nada: antes lo hacía al final, después de liberar el formato que el temporizador lee cada milisegundo.

## Plan

| Etapa | Objetivo |
|---|---|
| D0 | Herramientas en el PC y VM en modo de prueba ✅ |
| D1 | Compilar el ejemplo sin cambios de comportamiento e instalarlo en Windows 10 ✅ (verificado en Windows 10 22H2, build 19045, el 23/10/2026) |
| D2 | Nombres propios: «Sonari Virtual Microphone» y «Sonari Virtual Output», con identificadores de hardware de Sonari ✅ (verificado en Windows 10 22H2 el 23/10/2026) |
| D3 | Conectar la salida con el micrófono (buffer circular en el kernel): lo que Sonari reproduce, lo graba quien abra el micrófono ✅ (verificado en Windows 10 22H2 el 24/10/2026: más de 11 min con pausas, sin cortes ni pantallas azules) |
| D4 | Estabilidad: Driver Verifier, retirada en caliente, controles de volumen ✅ (verificado en Windows 10 22H2 el 24/10/2026, con Driver Verifier: desinstalar con música sonando, reinstalar sin reiniciar y volumen y silencio de los dos dispositivos). Pendiente: medir el retardo con DebugView para bajar de 30 ms |
| **D5** | **Sonari detecta su propio driver y lo prefiere a VB-CABLE** |
| D6 | Instalador y firma para distribuirlo (certificado EV y firma de Microsoft) |
