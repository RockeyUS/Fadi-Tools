# Fadi Tools

Convert, compress, edit and download files on Windows — offline, free, in English and Arabic.

**Download:** grab `Fadi.Tools.exe` from the [latest release](../../releases/latest) and open it. The first start downloads the free engines it uses (FFmpeg, ImageMagick).

## What it does
- **Convert** images, audio, video, documents and data files
- **Studio** — edit photos (text, background removal, crop) and videos (CapCut-style timeline, timed text, transitions, music, auto captions, projects)
- **Compress** — smaller files, same resolution; one-click Discord / Email / WhatsApp sizes; uses your graphics card when it can
- **PDF tools** — combine, reorder, rotate, delete, split, export pages
- **Download** from YouTube and 1,000+ sites
- **File inspector** and **Reverse engineer** for the curious

Settings and engines live in `%LOCALAPPDATA%\FileConverter`. A portable `.zip` can be made from Settings.

## Building
`bash build.sh` (Mono `mcs`, .NET Framework 4.5 WinForms). Output: `build/Fadi Tools.exe` (the one file to publish).

## Releasing a new version
1. Raise `Version` in `src/Shell.cs` and `src/AssemblyInfo.cs` (1.0.0 → 1.0.1 for fixes, 1.1.0 for new features). The app only offers an update when the release tag is higher than its own version.
2. Write `release/notes-<version>.md` with **both** parts:
   - a short description of the app (what it is, how to download and open it)
   - a changelog with **Added**, **Improved** and **Fixed** sections
3. On GitHub: Releases → Draft a new release → tag `<version>` → paste the notes → attach `Fadi Tools.exe` (used by the in-app updater) and the portable `.zip` → Publish.
