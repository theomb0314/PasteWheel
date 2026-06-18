# PasteWheel

A tiny Windows tray app: press a hotkey, a radial menu pops up at your cursor, pick an
item, and it's typed straight into whatever you were using. Your library is just files
and folders.

## Run it

1. Double-click **PasteWheel.exe**. A wheel icon appears in the system tray.
2. Press **Ctrl+Alt+W** anywhere. Pick an item. Done.

(First run needs the free [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
To build from source: `dotnet build -c Release`.)

## Use the wheel

- **Pick:** click a slice, or press its number **1–9**.
- **Open a folder:** click/number a slice with a `›`.
- **Back:** click the centre, or press **Backspace**. **Close:** **Esc**, or click outside.
- **Recent:** the chip in the centre jumps back to the last folder you pasted from (key **0**).

## Add your own pastes

Everything lives in `C:\Users\<you>\PasteWheel\Pastes\` (tray → **Open paste folder**).

- A **folder** = a sub-menu. A **.txt file** = one entry.
- The **file name** is the label; the **file contents** are what gets pasted.
- Put `01_`, `02_` in front of names to set the order.
- Drop a **.png/.jpg** in to paste an image. A `#hex` colour shows as a swatch.

## Settings

Tray menu: open/reload, **Change hotkey…**, toggle **Auto-paste**, **Start with Windows**.
Finer control lives in `C:\Users\<you>\PasteWheel\config.json`:

| Setting | Meaning |
|---|---|
| `Hotkey` | e.g. `Ctrl+Alt+W` (or use Change hotkey…) |
| `Size` | `small` / `medium` / `large`, or a number (px) |
| `Opacity` | `0.2`–`1.0` (colour swatches stay solid) |
| `AutoPaste` | `false` = just copy, you paste manually |
| `PasteMode` | `type` (most compatible) or `paste` (Ctrl+V) |

### Nice extras

- **Folder colour/icon:** drop a `_folder.json` in a folder: `{ "accent": "#FF5A1F", "icon": "🎨" }`
- **Shared wedge:** two folders with the same number (e.g. `02_A` and `02_B`) merge into one slice.
