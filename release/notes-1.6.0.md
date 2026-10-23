## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, find duplicates, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.6.0

### Added
- **Drag + Shift to convert.** Drag a file in File Explorer (or on the desktop) and hold **Shift**: a wheel opens under the mouse. Drop the file on a format in the outer ring to convert it, or on an action in the middle: **Compress**, **Remove background**, **Edit** (opens it in the Studio), **Copy text** or **Details** (File inspector). The new file is saved next to the original (in Downloads when that folder can't be written to) and shows in Activity, with a Windows notification when it's done. Works for pictures, videos, songs, documents and data files. Settings → Windows integration → "Quick convert wheel" turns it off.
- **Keep running by the clock.** A new setting, Settings → Windows integration → "Closing the window keeps Fadi Tools running by the clock": with it on, × hides the window instead of quitting, so the wheel stays ready. Click the icon by the clock to open it again, right-click → **Exit** to quit. It's off unless you turn it on (turning on the wheel offers to switch it on).
- **Close a file in File inspector.** An × at the top of the file card (or Esc) empties the page again, in both File details and Reverse engineer. The path box in Reverse engineer has its own × too.
- **See where engines are installed.** Reverse engineer shows the folder Node.js is installed in, with an **Uninstall** button. The background remover in the Studio (Edit → Background) shows its folder and size, with a **Remove** button; it downloads again the next time it's needed.

### Changed
- **A new app icon.** The letter ف (for Fadi), with a round head, a tail that curls into a little snake and a fez as its dot, on a soft orange tile. The small sizes (taskbar, by the clock, title bar) use a bolder drawing of it so it stays clear.
- **App logo: Light and Dark.** Settings → App logo now offers **Light** and **Dark** (the dark version is an orange ف on a charcoal tile). Silver is gone; anyone who had it now gets Light.
- **One Fadi Tools in the Start menu.** The separate "Image Viewer" entry is gone. Photos still open in Fadi Tools with a double-click or "Open with", which now just says "Fadi Tools".
- **The Studio's empty page** has the same drop area as Convert and Compress (a dashed box: click to choose a file or drop one in), and its toolbar only shows Open and PDF tools until something is open.

### Fixed
- Videos from Instagram, X and similar sites showed "unknown" for the download size. They now show the size, estimated from the video's bitrate or asked from the server.
- The fold arrow at the top of the side list jumped to a different place when the list folded or opened. It now stays in one spot.
- "Convert · Edit · Download" under the app's name was cut off; it now always shows in full. Page subtitles and the text in drop areas no longer get cut off in narrow windows either.
