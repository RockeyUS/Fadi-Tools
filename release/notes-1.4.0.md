## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.4.0

### Added
- **Subtitle language for auto captions.** Under "Spoken language" there's now "Subtitle language": keep the captions in the language that's spoken, or write them in English whatever is spoken.
- **Fold the side list.** The arrow at the top of the side list folds it down to icons (names show when you point at them) and back.
- **Counts in the side list.** Download shows how many downloads are running, and Activity shows how many tasks are running; a dot means something new finished.

### Improved
- **A cleaner look.** The side list is now a floating card with sections (Create, Files, Internet, App), thin separators and a gliding selection. Light mode has warmer colors, and the default accent is a new orange.
- **PDF tools are in Studio.** Studio has Photos, Videos and PDF tabs at the top. Everything from PDF tools (combine, edit pages, sign, number, watermark, lock) is under PDF.
- **Reverse engineer is in File inspector.** File inspector has "File details" and "Reverse engineer" tabs at the top, and "Analyze with REA" switches to that tab.
- **Auto captions are much more accurate.** A better speech model (Accurate now uses Whisper large-v3-turbo), more careful decoding, and the sound is cleaned up before it's read.
- **Everything is installed the first time.** Installing now also gets yt-dlp (Download page) and qpdf (PDF tools), so nothing asks to be downloaded later. Older installs get them in the background after updating.
- **New animations.** Picking a format in Convert makes the orange chip flow over to it and land with a soft ring. Clicking Activity sends a pulse from its icon, and the panel unfolds out of its row with the tasks sliding in one by one.
- **The app's folder is now called Fadi Tools.** Settings, engines and the installed app moved from `AppData\Local\FileConverter` to `AppData\Local\Fadi Tools`. This happens by itself on the first start; shortcuts and menus are updated.

### Fixed
- **Copy text kept Arabic words in reverse order** (for example "وزارة التعليم" came out as "التعليم وزارة"), and lines mixing Arabic and English came out jumbled. Words are now put in reading order, with English words inside Arabic text (and Arabic inside English) kept in the right order too.
- The buttons at the bottom of Settings overlapped the "Show tips" and "What's new" buttons when the window was narrow.

### Removed
- **Check online (VirusTotal)** in File inspector, and its key in Settings.
- **PDF tools** and **Reverse engineer** as separate pages in the side list (they're now tabs in Studio and File inspector).
