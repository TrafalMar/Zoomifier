# Zoomifier

Select a portion of the screen on one monitor and show it, magnified and live, on another monitor.

## Build & run

```
dotnet build src -c Release
src\bin\Release\net8.0-windows\Zoomifier.exe
```

Single-file exe: `dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`

## Usage

Every launch starts by asking you to drag a rectangle (on any monitor). The viewer then opens fullscreen
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

## How it works

A render thread drives a GPU pipeline paced by the viewer monitor's refresh rate (e.g. 144/240 fps):
it waits on a flip-model swap chain's frame-latency object (at most one frame queued), grabs the newest
desktop frame via DXGI Desktop Duplication, scales the region with Direct2D (nearest-neighbour, or
bilinear when "smooth" is on), draws the cursor and presents. Frame and cursor are presented together,
so the cursor never flickers. If duplication is unavailable (rotated monitor, secure desktop, …) the
region is captured with GDI and still scaled on the GPU; if the GPU path fails entirely, the viewer falls
back to plain GDI `StretchBlt`. The viewer, frame and hint windows use `WDA_EXCLUDEFROMCAPTURE`, so they
never appear in their own capture. The process is per-monitor-DPI aware, so all coordinates are physical pixels.
