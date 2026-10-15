# EventDeck v0.5 — Hide to Tray (pendiente de validación Windows)

## Base exclusiva
EventDeck-v0.5-RELEASE-CANDIDATE(2).zip, reenviado y confirmado por el operador.
Esta base contiene F7 LOOP y el mirror HwndSource/SharedImage. No contiene ProgramMonitorMirror DWM.
Se preservó ese pipeline exacto; no se recuperaron archivos de otras versiones.
Las dos grabaciones reenviadas coinciden byte a byte con las revisadas previamente (76,97 s y 39,62 s).
La primera muestra períodos de imagen detenida y recuperación en el monitor físico; la segunda muestra
movimiento en Output mientras otras aplicaciones cubren EventDeck. Estos registros y las pruebas del
operador motivan evitar minimizar; no demuestran por sí solos que Hide resuelva todos los casos.

## Cambios
- MainWindow.Tray.cs (nuevo): NotifyIcon con icono integrado de Windows, doble clic,
  Abrir EventDeck y Salir. Intercepta WM_SYSCOMMAND/SC_MINIMIZE antes de la minimización nativa.
  F9 global alterna Hide/Show con RegisterHotKey y MOD_NOREPEAT, sin polling.
  Conserva Normal/Maximized, restaura con Show/Activate y refresca solamente el monitor del operador.
  El icono es visible mientras la ventana está oculta. X y Salir invocan el cierre real existente.
  Libera NotifyIcon, menú, hook y registro F9 al cerrar. F9 se ignora durante un diálogo modal
  para no ocultar al propietario mientras se está editando un diálogo.
- MainWindow.xaml.cs: conecta inicialización/limpieza de bandeja. StateChanged actúa como defensa
  ante rutas que omitan SC_MINIMIZE: restaura inmediatamente el estado previo y oculta,
  con protección contra reentrada. Esta defensa puede recibir un estado minimizado transitorio;
  el botón nativo y F9 lo evitan de entrada. No deja la ventana minimizada.
  No actualiza geometría del mirror mientras MainWindow está oculta.
  F6 local y el botón existente llaman exactamente a ToggleVideoAudio.
- MainWindow.xaml: tooltip Salida con audio (F6). Retira F6/F9 del selector de fade-out
  para evitar colisiones. Las preferencias previas F6/F9 caen al valor predeterminado F8.
  Se mantienen las otras opciones anteriores de fade-out y su comportamiento.
- GlobalHotkeyRegistration.cs: ID por instancia, con el valor anterior predeterminado
  para F8 y un ID distinto para F9. No cambia la implementación del fade-out.
- HIDE-TRAY-NOTAS.md: este informe.

## F6: selección y Program
El video seleccionado tiene prioridad, igual que F7. Permite mutear B antes de enviarlo
sin cambiar el audio del video A que está en Program. Si la selección coincide con Program,
se aplica también al master con el SetProgramMuted existente. Sin selección de video,
actúa sobre el video de Program. Para mutear A cuando está seleccionado B, seleccioná A.
El botón y F6 muestran y modifican el mismo estado. No existe un segundo sistema de mute.
Solo se cambia ProgramMuted y, para el master correspondiente, Player.IsMuted por la ruta existente.
No se accede a AudioTrack, a las canciones, al volumen musical ni al crossfade.
F6 no es global. F7 conserva su código. F8 conserva el fade independiente.
Si Windows rechaza F9 por estar ocupado, se informa en el estado y la bandeja permite restaurar;
F9 también tiene fallback local. No se simula un registro global exitoso.

## Ocultar/restaurar
Hide/Show afectan exclusivamente MainWindow. No asignan Owner a Output ni lo ocultan.
No llaman Play, Pause, Stop, Seek, no cambian Source y no crean sesiones.
Output conserva su dispatcher y visuales originales. El refresco al restaurar solo actualiza
la geometría existente de Program Monitor y sus controles.
Referencia API: https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand

## Validación ejecutada
- Comparación byte a byte: VideoSession.cs, OutputWindow.xaml.cs, OutputWindow.xaml,
  OutputWindowHost.cs y AudioTrack.cs idénticos a la base.
- Comparación de métodos: ToggleVideoLoopFromHotkey y FadeOutCurrentAudioAsync intactos.
- XAML bien formado; FadeOutAudioButton conservado; Rect.Transform conservado.
- Revisión de rutas: minimizar/F9/tray no emiten órdenes de transporte; cierre usa la limpieza
  previa más la bandeja; F6 y botón comparten método y no acceden al audio independiente.
- dotnet restore EventDeck.csproj: no ejecutable dotnet (exit 127).
- dotnet build EventDeck.csproj: no ejecutable dotnet (exit 127).
NO se afirma compilación ni ejecución de WPF en Windows.

## Aceptación pendiente en Windows
1. dotnet restore y dotnet build desde EventDeck.
2. Video con audio: normal y cubierto por Chrome durante 2 minutos.
3. Botón minimizar: icono en tray, Output visible; esperar 1 minuto y restaurar con doble clic.
4. F9: 10 ciclos de ocultar 30 segundos/restaurar. Probar Normal y Maximized.
5. F6 antes de enviar B y durante B; comprobar botón, silencio y continuidad de posición.
6. F6 con canción independiente: la música no cambia. F8: la música hace fade y video sigue.
7. F7 LOOP; MERGE, CUT, FADE, preload/first-frame, teclado y operaciones mixtas.
8. Salir desde tray y X: se cierran Output y proceso; desaparece icono y se liberan hotkeys.
9. Observar físicamente Output durante todo el ensayo, no solamente Program Monitor.
Solo después de superar estas pruebas corresponde declarar v0.5 Stable.
