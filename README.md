# EventDeck v0.5

Control multimedia de escritorio para actos y eventos presenciales. Parte exclusivamente de la v0.4 corregida y conserva C#, WPF, .NET 10, WebView2, bins, proyectos JSON y `OutputWindow`.

## Cambios de v0.5

- Los botones AUDIO PREVIEW y SALIDA CON AUDIO ahora ocupan una fila propia y tienen ancho suficiente para mostrar sus textos completos. Los estados de música y de la pista preparada tienen más separación y un fondo propio.
- MERGE ahora hace un fundido cruzado limpio: la imagen saliente se desvanece mientras la nueva aparece, con una curva suave y sin desplazamientos o zoom. La transición sigue usando la captura saliente y la sesión de video ya preparada de v0.4.
- El selector de pantallas tiene un desplegable oscuro con estados de selección y foco a juego con la interfaz. Las barras de desplazamiento usan un thumb oscuro con el color de acento al pasar el puntero o arrastrarlo.
- ↑ y ↓ recorren los recursos de la biblioteca. Cada selección actualiza Preview: los videos se cargan y quedan preparados; las pistas de audio se preparan para PLAY y se escuchan como vista previa en Preview, respetando el mute de Preview. La vista de audio tiene controles de play, pausa, detención, búsqueda y mute propios. La vista previa no envía el recurso a Output ni detiene ni reemplaza las pistas que ya están sonando. PLAY inicia la pista preparada desde el comienzo y aplica el fundido existente.

## Arquitectura y compatibilidad

- Output y PROGRAM continúan compartiendo la escena y el reproductor maestro de `VideoSession`; no se agrega un decoder para imitar la salida. La sesión de Preview se conserva separada.
- La carga de video, `MediaOpened`, el preload y la preparación del primer cuadro se mantienen. LOOP continúa desactivado por defecto y se configura por recurso.
- Los volúmenes elegidos por el usuario siguen separados del gain de fundido; cero sigue siendo silencio real.
- Se preservan las correcciones de la v0.4 corregida para los namespaces WPF/WinForms, `System.IO.InvalidDataException`, diálogos WPF, drag and drop y geometrías WPF (`RectangleGeometry`). No se reemplazó `VideoSession` ni se reintrodujo la sincronización periódica entre reproductores.
- Se mantienen CUT, FADE, MERGE, BLACK, bins, guardado de eventos y captura web estática. Los eventos conservan rutas a medios; no copian archivos.

## Requisitos y ejecución

- Windows 10/11 x64, .NET 10 SDK y WebView2 Evergreen Runtime para páginas web.
- Para una salida de evento, conectar el proyector y configurar Windows en **Extender estas pantallas**.

En PowerShell, dentro de la carpeta `EventDeck`:

```powershell
dotnet restore
dotnet build
dotnet run
```

Para publicar una build autocontenida:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish\win-x64
```

## Verificación y límites

- En este entorno Linux no está disponible el SDK de .NET ni un compilador WPF, por lo que esta versión no se pudo compilar ni ejecutar aquí. Se realizaron verificaciones estáticas de XAML, handlers, namespaces sensibles y del ZIP; la compilación WPF en Windows queda pendiente.
- WPF depende de los codecs de Windows para los formatos de video y audio. Conviene probar los archivos concretos en la PC del evento.
- Si falla la captura de una página web, Output puede mostrarla en vivo y PROGRAM puede quedar negro.
- WPF compone Output y PROGRAM según el ciclo de presentación de cada ventana; comparten la fuente de imagen, pero no garantizan que dos pantallas físicas muestren cada cuadro en el mismo instante.
