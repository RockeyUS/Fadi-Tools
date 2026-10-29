## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, find duplicates, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.8.0

### File inspector: is this file safe?
- **A verdict for every file.** The file card shows **Looks safe**, **Be careful** or **Dangerous**. Click it (or open the new **Safety** tab) to see why.
- **What it checks:** names that hide their real extension (`invoice.pdf.exe`, invisible flipped text, long runs of spaces), programs dressed up as pictures or documents, files Windows marked as downloaded (and from which site), who signed a program and whether the signature is still valid, Office macros and embedded objects, PDFs that run scripts or start programs, programs and locked files inside zips, zip bombs, shortcuts that start a command, and data added after the end of a file.
- **Look up online.** One click opens the file's VirusTotal page, where 70+ antivirus programs give their verdict. Only the file's fingerprint (SHA-256) is sent, never the file.
- **Unblock** removes Windows' "downloaded from the internet" mark when you trust the file. **Save the extra data** takes out whatever was hidden after the end of a file.

### Hidden details
- A new **Hidden details** tab lists what a file says about you: where a photo was taken (with altitude), the phone or camera, its serial number, the owner, the editing app, the author and company of Word, Excel and PowerPoint files, comments and tracked changes, PDF author and dates, and video location and device tags.
- **Show on map** opens the place a photo was taken.
- **Remove personal info** saves a clean copy next to the original. Photos are cleaned without re-compressing and stay the right way up. Office files, PDFs, videos and songs are cleaned too.

### Look inside
- **Inside** tab for zip, Word, Excel, PowerPoint, APK, JAR and ISO files: every file with its size, programs marked in orange. **Inspect** a file inside without unpacking (a **Back** button returns to the archive), save one file, or **Extract all**.
- **Program** tab for .exe and .dll files: product, publisher, version, 32 or 64-bit, .NET or native code, build date, what it can do (use the internet, start programs, watch the keyboard, capture the screen and more) and the files it uses. Its icon is shown with **Save icon** (.ico or .png).
- **Videos and songs** show every video, sound and subtitle track with its language, chapters, bitrate, HDR and rotation. **Pictures** show the color profile, bit depth, DPI, see-through parts and frame count. **Certificates**, **shortcuts**, **web links** and **fonts** (with an English and Arabic sample) show what's in them.
- **Hex** tab: find text or bytes (`4D 5A`), go to any position (`0x1F00`, `50%`), and a colored map of the whole file shows where it's packed or encrypted. Click the map to jump there.

### Compare, check and report
- The **⋯** button on the file card adds **Compare with another file** (same or not, where they differ, and the changed lines for text files), **Check a checksum** (paste MD5, SHA-1, SHA-256 or SHA-512 from a download page), **Copy report** and **Save report**.
- **Drop a folder** on the File inspector to see what it holds by type, its largest files, and files whose extension doesn't match what's inside, with **Fix all**.

### Merge videos from the donut wheel
- Drag several videos (or songs) and the wheel offers **Merge into one**. A window lets you set the order (drag, by name or by date, and reverse), the name, how clips meet (straight cut, cross fade or through black), the shape (like the first, 16:9, 9:16 or 1:1) and whether to keep the sound. **Preview** plays them in order first.
- When the clips already match, **Quick join** puts them together in seconds without re-encoding.

### Fixed
- **Copying a picture's address from a browser.** "Copy link address" on Google Images (and Bing, Yandex, DuckDuckGo and Yahoo) gives a link to the results page, not the picture; Fadi Tools now finds the picture inside it. Links without an extension (like `…/photo?id=12`) and pictures copied as data also download. Copying a search page link explains what to copy instead.
- **Play and pause in the video studio.** Pressing play and pause quickly right after adding a video no longer freezes the picture for the first seconds.
