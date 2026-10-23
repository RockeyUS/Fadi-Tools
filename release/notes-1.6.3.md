## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, find duplicates, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.6.3

### Added
- **Edit the text in PDFs.** Studio → PDF has a new **Edit text** tool. It shows each page with the text Windows finds on it.
  - **Edit text:** click a line and type over it. Enter keeps the change, Esc puts the line back. Each change can have its own letter size, Plain or Classic letters, Bold, and colour.
  - **Add text:** click anywhere on the page to write something new.
  - **Erase:** drag over anything to wipe it clean with the page's own colour.
  - **Save as PDF** makes a new PDF. Everything you didn't touch stays exactly as it was (sharp text, pages, links), and the new words can be searched and copied.
  - Changed text is drawn over the page, so the old words are hidden but still in the file underneath. Finding the lines uses Windows' own text reader (Windows 10 or 11).
- **AI Scale on the Donut Wheel.** It makes a picture 2×, 3× or 4× bigger, with the detail redrawn by AI (Real-ESRGAN) instead of stretched.
  - Pick **Photo** or **Drawing**; the window shows the new size in pixels.
  - The first time, its engine is downloaded (about 45 MB, one click). It uses the graphics card; without one it can use, it tries the processor (much slower).
  - When it's done, the window grows and shows the whole picture before and after, with a handle to drag across. Scroll to zoom in where the mouse is (up to 4× the result's pixels), drag to look around, and double-click to switch between the whole picture and 1:1.
- **Resize and Rotate on the Donut Wheel**, each in a small window of its own:
  - **Resize:** width and height (the shape is kept unless you undo the chain), or quick sizes 25%, 50%, 75%, 150% and 200%.
  - **Rotate:** turn left or right and flip both ways. The picture turns as you click, then **Save**.
- **Trim on the Donut Wheel** for videos and songs. A strip of frames (or the song's waveform) with a start and an end to drag, the frame where you are shown above it, and **Play this part**. **Quick cut** skips re-encoding: much faster, but it cuts at the nearest keyframe.
- **Several files at once on the wheel.** Drag several pictures and the middle ring offers **Combine into PDF**; drag several PDFs and it offers **Merge PDFs**. The new PDF is saved next to the first file. One-file actions (Crop, Trim, Copy text…) step aside when several files are dragged.
- **Recipes.** Several steps done in one go, as one slice of the Donut Wheel; for example "Square JPG": crop 1:1 → resize to 1080 px → JPG → 60% of the size.
  - They're made on the Donut Wheel page (**New recipe**), for pictures, videos or songs.
  - Steps: Crop, Resize, Rotate, Remove BG, Convert and Compress. Move them up and down, and pick each step's setting from its chip.
  - A new recipe goes on the wheel straight away. "Square JPG" is there to start with.
- **Watch folders.** A new card in Settings sets them up.
  - Pick a folder and say what to do when a kind of file lands in it: convert it, compress it, or run a recipe. For example, every HEIC photo in Downloads becomes a JPG by itself.
  - The new file is saved in the same folder, and the original stays as it is.
  - Folders are watched while Fadi Tools is open or waiting by the clock, and each rule can be paused.
- **Start with Windows.** A new setting in Windows integration: "Start Fadi Tools when Windows starts". Fadi Tools then waits by the clock without opening a window, so the Donut Wheel and watch folders are ready. It's off unless you turn it on.
- **A first-time hint for the Donut Wheel.** The first few times you drag a file in File Explorer, a small note glides after the mouse: "Shift for the Donut Wheel". It stops once you've used the wheel. The welcome tour has a new page about the wheel too, acted out.

### Changed
- **Compress has a size slider, on the wheel and on the Compress page.** 50% makes the file half its size and 200% makes it twice as big (for pictures, videos and songs). The new size shows in big numbers and glides as you move the slider, with "65% smaller" or "1.5× bigger" under it, and the slider remembers where you left it. PDFs and Office files keep Light / Balanced / Strong.
  - On the Compress page it's the new **New size %** tab, with the new size of all the listed files together; **Quality & limit** has Light / Balanced / Strong and the MB limits (Discord, Email…) as before. PDFs and Office files in the list are made smaller the usual way.
- **The Donut Wheel follows Shift.** It stays open for as long as you hold Shift during the drag, even before the mouse moves onto it; it used to vanish after about two seconds. Let go of Shift and it's gone in a blink (about a seventh of a second); press Shift again (still dragging) and it comes back. After a drop, the chosen slice glows for a moment and the wheel shrinks and fades away smoothly.
- **PDFs open with the PDF tools.** A PDF dropped on (or opened in) the Studio goes straight to its pages and all the PDF tools (Edit text, Add text & pictures, turn and delete pages, sign, number, lock…) under Photos / PDF, instead of a separate screen. **Close** closes it.
- **"Remove background" is "Remove BG"** on the wheel.
- **The middle ring holds up to 8 actions** (it was 6). Pictures start with Compress, Crop, Resize, AI Scale, Remove BG, Edit, Copy text and Details. A wheel you set up before gets AI Scale and Resize (and Trim for videos and songs) added once, when there's room.
- **The panel by the clock is smaller**, like a menu. **Customize** is a small pencil next to the Donut Wheel switch. It closes as soon as you click anywhere else, so it never stays in the way of the taskbar, the hidden-icons box or other apps. Opened from the hidden-icons box, it appears beside the box instead of on top of it.
- **"Show" under "Done" in Convert.** Each converted file has a **Show** button under its Done, which opens its folder with the file picked.
- **Quieter notifications.** Windows notifications now come only when a task took a while (over 20 seconds) or failed, and without a sound. Both are new switches under "Windows notification…" in Settings → Windows integration ("Only when it took a while…" and "Play a sound…"); the notification itself can still be turned off there or in the panel by the clock. Quick conversions from the Donut Wheel no longer ping every time.
- **Removing Node.js** (File inspector → Reverse engineer) removes it straight away instead of opening Windows Settings. Windows asks once for permission. A Node.js that was just unzipped into a folder is deleted with that folder.

### Fixed
- In Crop, switching between ratios made the frame smaller each time. A ratio now gives the biggest frame of that shape the picture holds.
- GIF links copied in Discord didn't download. Discord's GIFs (KLIPY links), its picture-proxy links and its resized attachment links now download as the GIF itself, in full size. KLIPY only lets real browsers in, so Fadi Tools now opens the link in a real Microsoft Edge window of its own, kept off the screen with an empty profile, waits for the page to be there, and takes the GIF from it; then that window closes. Other sites that turn away downloaders are tried the same way.
- In Activity, after **Clear finished**, the "Nothing here yet" text was cut off. The panel now fits it in full.
- In the Studio's Background tab, the folder the background remover is installed in was cut off. It now shows in full, over two lines, with the size and Remove on the line above.
