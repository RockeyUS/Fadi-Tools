## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, find duplicates, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.5.0

### Added
- **Duplicate finder** (new page under Files). Pick one or more folders and it finds files that are exactly the same, even when their names differ, in groups. Choose which copy stays (or "keep the oldest / newest" in one click) and move the extra copies to the Recycle Bin, where Windows can bring them back. It can look at everything, or only photos and videos, music or documents, with or without subfolders.
- **Searchable PDFs.** PDF tools → More → **Make the text searchable** reads every page and adds a hidden text layer, so scanned PDFs can be searched, selected and copied in any PDF reader. The pages look exactly the same. Works in English and Arabic.
- **Rename by date or song info.** A new **Date & tags** way in Rename: photos and videos named by when they were taken (date, date and time, or date before the old name), songs by their tags (Artist - Title, 01. Title, or Title). Files from the same day get 01, 02…
- **Keeps working when closed.** Closing the window while something is converting, compressing or downloading hides it next to the clock; the work carries on and a note tells you when everything is done. Click the icon to come back, or right-click → Exit. Can be turned off in Settings.
- **Copy a link, get a download.** Copy a YouTube (or other video) link anywhere and Fadi Tools offers to download it. Can be turned off in Settings.
- **Keyboard and screen readers.** Tab moves through every button, switch, slider and list; arrows move between tabs, choices and the side list; Enter or Space presses; a ring shows where you are. **Ctrl+1…8** opens the pages. Screen readers (Narrator, NVDA) read the name, kind and state of every control and list row.
- **Sharp on every screen.** The app now follows each screen's own size setting: moving the window to a screen with a different scale redraws it at the right size instead of stretching it.
- **Self-test and automatic build check** for the project: every change on GitHub is built on Windows and checked page by page. **Ctrl+Alt+Shift+S** saves fresh pictures of every page (with made-up example files) for the project page.
- **winget** files, so the app can be added to Windows' own package catalog (`winget install RockeyUS.FadiTools` once it's accepted).

### Improved
- **Photos / PDF.** The PDF tab is now part of Photos, renamed **Photos / PDF**: a PDF opens there like a photo (read, add text and pictures, copy text, save), and **PDF tools** (or **Pages** with a PDF open) gives the page tools (combine, reorder, rotate, sign, number, watermark, lock, split), with **Back** to return.
- **Pictures on a PDF page** can be stretched from their sides now, not only resized from the corners.
- **GIF downloads** show their expected file size before you download, and while the GIF is being made.
- **Copy text is more accurate.** Small print is read at double size, the reader picks the best of several reads, and a "]" at the end of a line is no longer read as "1" (so "[Total Mark for this Assignment is 8]" comes out right).
- **The copy text window can be moved** (drag it by the top), like the other small windows.
- **Activity opens and closes faster.**
- **Tabs** (Image / Audio / Video in Convert and everywhere else): the highlight under the mouse now sits evenly inside each tab.

### Fixed
- Copy text found nothing on a page that had been turned (rotated); it now reads turned pages and pictures.
