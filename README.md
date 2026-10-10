# Zoomifier

Select a portion of the screen on one monitor and show it, magnified and live, on another monitor.

## Build & run

```
dotnet build src -c Release
src\bin\Release\net8.0-windows\Zoomifier.exe
```

Single-file exe: `dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`

## Usage

On first launch you are asked to drag a rectangle (on any monitor). The viewer then opens fullscreen
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

Settings are saved to `%AppData%\Zoomifier\settings.json`.

## How it works

Each frame (~60 fps) the region is `StretchBlt`-ed straight from the desktop DC into the viewer window
(GDI+ bilinear when "smooth" is on), and the mouse cursor is drawn on top, scaled. The viewer and
frame windows use `WDA_EXCLUDEFROMCAPTURE`, so they never appear in their own capture. The process is
per-monitor-DPI aware, so all coordinates are physical pixels.
