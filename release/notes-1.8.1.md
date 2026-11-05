## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, find duplicates, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.8.1

### Fewer false virus warnings
- **Built with Microsoft's own C# compiler.** Fadi Tools.exe now has the same shape as a program built with Visual Studio: a Windows manifest, a real build date and the usual sections. Some antivirus "machine learning" checks were suspicious of the old shape.
- **No more alarming words inside the app.** The File inspector's list of Windows functions that malware uses (to tell you what a program can do) is now stored as fingerprints instead of plain text. Some antivirus rules look for those names in any file, so they took the inspector for the thing it detects.
- **Reading text in pictures and showing PDF pages** start Windows PowerShell with the gentler "RemoteSigned" setting instead of "Bypass".

Nothing else changed: everything from 1.8.0 works the same.

