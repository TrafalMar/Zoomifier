# Zoomifier

Select a portion of the screen on one monitor and show it, magnified and live, on another monitor.

## Build & run

```
dotnet build src -c Release
src\bin\Release\net8.0-windows\Zoomifier.exe
```

Single-file exe: `dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`

## Usage

The last selected area is restored on launch; on first launch (or if that area is no longer on screen) you
are asked to drag a rectangle (on any monitor). The viewer then opens fullscreen
on a *different* monitor. A red frame marks the source region (it is not captured).

| Input | Action |
|---|---|
| Ctrl+Alt+Z (global) / R | Select a new region |
| Mouse wheel, `+` / `-` | Zoom in / out (wheel zooms around the pointer) |
| Left-drag, arrow keys | Pan the source region (Shift+arrow = 1 px) |
| F11 / double-click, Esc | Toggle / leave fullscreen |
| M | Move viewer to next monitor |
| A / S / C / B | Keep aspect · smooth (bilinear) scaling · show cursor · show region frame |
| Right-click / tray icon | Menu (monitor picker, toggles, exit) |
| H / click the hint | Switch the shortcut hint between the compact badge and the full panel |
| Shift+H | Hide / show the shortcut hint |

A shortcut cheat-sheet sits in the viewer's bottom-right corner (excluded from capture, never takes focus);
it lists every key and shows which toggles are on.

Settings are saved to `%AppData%\Zoomifier\settings.json`.

## Changing shortcuts

Right-click the viewer or tray icon → **Keyboard shortcuts…**. Click a shortcut, press the new keys, Save.
Global shortcuts (work in any app) need Ctrl or Alt (or an F-key); a key already used by another action is
moved, and global ones taken by another app are flagged. The lens hold key is picked there too.

Only changed shortcuts are saved (in `%AppData%\Zoomifier\settings.json`), so they survive updates, and
new shortcuts or defaults in later versions still apply. If the file can't be read it is kept as
`settings.json.bak` instead of being overwritten.

## Lens mode

Switch either way with **Ctrl+Alt+L** (works in any app), **L** in the viewer, or one click on
*Viewer on another monitor* / *Lens over the area* in the menu (each launch starts in the other-monitor
mode). The viewer hides and the zoom
appears in place: a click-through window lies exactly over the selected area and shows its centre magnified
(never takes focus, excluded from capture, so games and apps keep working underneath).

| Input (global, works in any app) | Action |
|---|---|
| Ctrl+Alt+L | Switch between viewer and lens mode |
| Ctrl+Alt+X | Lens on / off |
| Hold key (default: mouse back button) | Show the lens while held; pick the key under *Hold to show* in the tray menu |
| Ctrl+Alt+Plus / Minus | Lens zoom in / out (presets in the tray menu) |
| Ctrl+Alt+Z | Select a new area |

Switch back with Ctrl+Alt+L or *Viewer on another monitor* in the tray menu.

## How it works

A render thread drives a GPU pipeline paced by the viewer monitor's refresh rate (e.g. 144/240 fps):
it waits on a flip-model swap chain's frame-latency object (at most one frame queued), grabs the newest
desktop frame via DXGI Desktop Duplication, scales the region with Direct2D (nearest-neighbour, or
bilinear when "smooth" is on), draws the cursor and presents. Frame and cursor are presented together,
so the cursor never flickers. If duplication is unavailable (rotated monitor, secure desktop, …) the
region is captured with GDI and still scaled on the GPU; if the GPU path fails entirely, the viewer falls
back to plain GDI `StretchBlt`. The viewer, frame and hint windows use `WDA_EXCLUDEFROMCAPTURE`, so they
never appear in their own capture. The process is per-monitor-DPI aware, so all coordinates are physical pixels.
