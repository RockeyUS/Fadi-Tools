## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.4.2

### Added
- **PDFs open in the PDF tab.** Dropping or opening a PDF anywhere in Studio (also on the Photos side) takes you to the PDF tab. There you can do everything the Photos side did with it: read it page by page, add text and pictures, copy its text and save it as a PDF.
- **Pages button (PDF tab)** to go from the page editor to the page tools (reorder, rotate, delete, split, sign, number, watermark, lock) with the same PDF, and **Add text & pictures** in the page tools to go back, starting on the selected page.
- **Search animation:** the quick search grows out of the search box and shrinks back into it.
- **Clear animation:** Clear (Convert, Compress, Rename, PDF, Download, Activity) sweeps the rows or pages away one after another.

### Improved
- **Smooth in full screen.** Page changes, tab changes, the light/dark circle and the language fade are now drawn by the graphics card on a see-through layer, so they stay smooth however big the window is. Animations follow the clock, so they no longer slow down when the PC is busy.
- **Folding the side list** slides the page along instead of redrawing it on every step.
- **Activity animation, redone.** The panel grows out of the Activity row with a small spring and shrinks back into it when it closes; the Activity icon sends out a pulse.
- **Small windows.** Settings switches to one column when the window is narrow, the Node.js status in Reverse engineer gets its own row, the PDF page tools get tighter (and turn into icons on short windows) so every tool stays reachable, and the PDF buttons next to the tabs become icons.

### Fixed
- Scrolling Settings quickly up and down made the page wobble (the buttons moved separately from the cards). The whole page now scrolls as one piece.
- The Uninstall button covered the version text and the "Show tips" / "What's new" buttons in a small window.
- The "Install Node.js" button and its status piled up on the other buttons in a small window.
- Closing a PDF left its page strip at the bottom, with pages that could no longer be clicked.
- Opening another document over one with unsaved page edits now asks first.
