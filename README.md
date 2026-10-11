# Zoomifier

Zoom into any part of your screen, live and smooth. Show the magnified area on another monitor,
or right on top of the area itself like a scope.

## Download

Get the latest version from [Releases](https://github.com/TrafalMar/Zoomifier/releases):

| File | When to use it |
|---|---|
| `Zoomifier-vX.Y.Z-win-x64.exe` | Small download; needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `Zoomifier-vX.Y.Z-win-x64-selfcontained.exe` | Larger, but runs without installing anything |

Requires Windows 10 (version 2004 or newer) or Windows 11, 64-bit. No installation needed: just run the exe.

## Getting started

1. Start Zoomifier. On the first launch, drag a rectangle around the area you want to zoom (on any monitor).
2. The magnified view opens fullscreen on a *different* monitor and updates live at your monitor's refresh rate.
3. A red frame marks the area being zoomed.

Next time, Zoomifier reopens the area you used last. Press **Ctrl+Alt+Z** at any time to pick a new one.
While selecting, **Esc** or right-click cancels.

Zoomifier runs from the tray icon. Right-click the viewer or the tray icon for the menu; **Exit** is there too.

## Two modes

| Mode | What it does |
|---|---|
| **Viewer on another monitor** | The zoomed area is shown in its own window, fullscreen or windowed, on the monitor of your choice. |
| **Lens over the area** | The zoom appears right over the selected area, like a magnifying glass. Clicks go through it, so games and apps keep working underneath. |

Switch between them with **Ctrl+Alt+L** (works in any app), **L** in the viewer, or one click in the menu.
Zoomifier always starts in viewer mode.

## Viewer mode

| Shortcut | Action |
|---|---|
| Mouse wheel | Zoom in / out around the pointer |
| `+` / `−` | Zoom in / out around the centre |
| Left-drag | Move the zoomed area |
| Arrow keys | Move the zoomed area by 5% (hold **Shift** to move by 1 pixel) |
| F11 or double-click | Toggle fullscreen |
| Esc | Leave fullscreen |
| M | Move the viewer to the next monitor |
| A | Keep aspect ratio on / off |
| S | Smooth scaling on / off (off = sharp pixels) |
| C | Show the mouse cursor in the zoom on / off |
| B | Show the red frame on / off |
| R | Select a new area |
| L | Switch to lens mode |

The menu also has a monitor picker, so you can send the viewer to a specific screen.

### Shortcut panel

A cheat-sheet in the viewer's corner lists every shortcut and shows which options are on or off.

- **H**, or a click on the panel, switches between a small badge and the full list.
- **Shift+H** hides or shows it completely (also in the menu).

## Lens mode

| Shortcut (works in any app) | Action |
|---|---|
| Ctrl+Alt+X | Lens on / off |
| Hold the mouse back button | Show the lens only while held |
| Ctrl+Alt+Plus / Ctrl+Alt+Minus | Zoom the lens in / out |
| Ctrl+Alt+Z | Select a new area |
| Ctrl+Alt+L | Switch back to viewer mode |

In the menu you can also:
- pick a lens zoom from 1.5× to 8×;
- turn the lens on or off;
- choose a different hold key.

The red frame appears around the lens only while the lens is visible.

## Changing shortcuts

Right-click the viewer or the tray icon → **Keyboard shortcuts…**

- Click a shortcut and press the new key combination, then **Save**.
- **✕** removes a shortcut. **Reset to defaults** restores them all.
- For the lens hold key, click its box and press any key or mouse button, including mouse side buttons.
- Global shortcuts (the ones that work in any app) need **Ctrl** or **Alt**, or an F-key.
- If a combination is already used by another action, it moves to the new one and you're told which action lost it.
- If another app already uses a global shortcut, it's marked in red so you can pick a different one.

The menus and the shortcut panel always show your current keys.

## Settings

All settings are saved automatically: the selected area, options, lens zoom and shortcuts.
They are kept when you update to a newer version.

## Good to know

- Zoomifier's windows don't appear in screen sharing, recordings or screenshots (Discord, OBS, Teams, etc.).
  This is what lets the zoom show what's underneath instead of zooming itself.
- Only one copy of Zoomifier runs at a time.

## Build from source

```
dotnet build src -c Release
src\bin\Release\net8.0-windows\Zoomifier.exe
```
