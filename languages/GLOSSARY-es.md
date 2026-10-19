# Spanish glossary

Every translator of `es.json` follows this, so one idea has one wording on every page (as WORDING.md does for English).

## Voice

- Neutral Spanish for Spain and Latin America alike. `tú`, never `usted` or `vos`. Short and plain, like the English. No exclamation marks.
- Sentence case as in the English. A text in capitals in English (chips, card labels like `CPU PEAK`) is in capitals in Spanish.
- Keep every hole (`{0}`, `{1:0.0}`, `{2,8}`) exactly, each one used once or more, none added. Move holes to wherever Spanish word order needs them. Keep doubled braces `{{` `}}` as they are.
- Keep the English text's leading or trailing spaces and punctuation (a text starting with ", " stays starting with ", ").
- Product and part names stay: RigSight, CPU, GPU, RAM, VRAM, NVMe, SSD, BIOS, Windows, RivaTuner, Steam, Epic, NVIDIA, AMD, Intel, FPS, Wi-Fi.
- Units stay: °, °C, °F, W, kWh, GHz, MHz, MB, GB, TB, KB, MB/s, Mbps, RPM, rpm, V, A, ms, fps. Durations: keep `h`, `m`, `s` (`2h 35m`).
- A text that is a single English word used as a label is translated as a label (noun or short phrase), not a sentence.

## Words

| English | Spanish |
|---|---|
| Home | Inicio |
| Reports | Informes |
| Apps / app | Aplicaciones / aplicación (plural "apps" in tight labels is fine: "apps") |
| Games / game | Juegos / juego |
| Processes | Procesos |
| Crashes (page) | Fallos |
| Timeline | Cronología |
| Temperatures | Temperaturas |
| Fans | Ventiladores |
| Memory | Memoria |
| Storage | Almacenamiento |
| Network (page) | Red |
| Internet (amounts) | Internet ("Internet 66 GB") |
| All sensors | Todos los sensores |
| Dashboard(s) | Panel(es) |
| Widgets | Widgets |
| Overlay | Superposición |
| Taskbar | Barra de tareas |
| Settings | Ajustes |
| On screen | En pantalla |
| Hardware | Hardware |
| sensor | sensor |
| reading | lectura |
| crashed (an app) | se cerró por un fallo ("Dota 2 se cerró por un fallo a las 9:14 PM") |
| app crash (noun) / chip APP CRASH | fallo de aplicación / FALLO DE APLICACIÓN |
| froze / FROZE / freeze | se congeló / SE CONGELÓ / congelación |
| The PC froze. | El PC se congeló. |
| blue screen / BLUE SCREEN | pantalla azul / PANTALLA AZUL |
| sudden shut-off / SUDDEN SHUT-OFF | apagado repentino / APAGADO REPENTINO |
| The PC shut off suddenly | El PC se apagó de repente |
| graphics driver reset / GRAPHICS DRIVER RESET | reinicio del controlador gráfico / REINICIO DEL CONTROLADOR GRÁFICO |
| graphics driver | controlador gráfico |
| driver | controlador |
| power cut / likely a power cut | corte de luz / probablemente un corte de luz |
| problems / No problems | problemas / Sin problemas |
| PC problems | problemas del PC |
| CONFIRMED / LIKELY / POSSIBLY / NOT ENOUGH TO SAY | CONFIRMADO / PROBABLEMENTE / POSIBLEMENTE / NO HAY SUFICIENTE PARA DECIRLO |
| likely / possibly (in a sentence) | probablemente / posiblemente |
| Not enough on record to say why. | No hay suficientes datos para saber por qué. |
| Average / Highest / Lowest | Promedio / Máximo / Mínimo |
| CPU peak / GPU peak | Pico de CPU / Pico de GPU (capitals: PICO DE CPU) |
| your usual | lo habitual / tu habitual ("49m menos que tu miércoles habitual") |
| as usual | como siempre |
| the last 7 days / the 7 days before | los últimos 7 días / los 7 días anteriores |
| hotter / cooler | más caliente / más fría (agree with the noun: la CPU, la GPU → "más caliente") |
| at rest | en reposo |
| in game / while playing X | en juego / mientras jugabas a X |
| active / away / PC on | activo / ausente / PC encendido |
| Today / Yesterday | Hoy / Ayer |
| This week / Last week / This month / Last month | Esta semana / La semana pasada / Este mes / El mes pasado |
| Day / Week / Month / Year / Custom | Día / Semana / Mes / Año / Personalizado |
| Click for the full picture. | Haz clic para ver todo. |
| Not recorded / No reading | Sin registrar / Sin lectura |
| installed / updated / removed (a change) | instalado / actualizado / eliminado ("Mage Arena instalado", "Copilot actualizado a 26.01") |
| update (RigSight) | actualización |
| Update / Restart / Try again / Open / Close / Cancel / Save / Delete | Actualizar / Reiniciar / Intentar de nuevo / Abrir / Cerrar / Cancelar / Guardar / Eliminar |
| Export… | Exportar… |
| Copy report | Copiar informe |
| What's new | Novedades |
| drive (storage) / the C drive | unidad / la unidad C |
| full (a drive) | lleno ("73% lleno") |
| fan speed | velocidad del ventilador |
| pump | bomba |
| motherboard | placa base |
| power supply | fuente de alimentación |
| admin rights | permisos de administrador |
| standard account | cuenta estándar |
| background agent | agente en segundo plano |
| tray | bandeja del sistema |
| notification | notificación |
| shortcut (keys) | atajo |
| history (recorded data) | historial |
| language pack | paquete de idioma |
| The latest language pack couldn't be downloaded. | No se pudo descargar el paquete de español más reciente. |

## Times

- Times and dates come in through holes, already formatted by Windows: never write a time format yourself.
- "at {0}" before a time: "a las {0}". "on {0}" before a day: "el {0}". "since {0}": "desde {0}".
- "{0} ago": "hace {0}".
