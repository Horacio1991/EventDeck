# EventDeck v0.7 — gestión del evento

Esta versión recupera la organización por pestañas/bins de v0.6C y conserva la búsqueda, los filtros, la indicación y revinculación de archivos faltantes, y el estado global del evento.

- Cada pestaña contiene su propio conjunto ordenado de recursos. Se pueden crear, renombrar y eliminar pestañas; eliminar pide confirmación, conserva al menos una pestaña y aclara que solo quita sus referencias, no los archivos originales.
- La búsqueda y el filtro se aplican a la pestaña seleccionada. El indicador de archivos faltantes sigue revisando todos los bins del evento.
- Los elementos temporales “Sección” de proyectos guardados por FIX1/FIX2 se descartan al abrirlos. Los recursos válidos del evento y los bins se conservan.
- El cuadro de nombre usa terminología de pestañas y un campo de texto compacto con alineación vertical centrada.

Los números existentes de `MediaKind` se mantienen (`Image` a `WindowCapture` conservan sus valores 0–4). No se modificó el motor multimedia ni se agregaron dependencias.
