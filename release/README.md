# Fadi Tools

Convert, compress, edit and download files on Windows — offline, free, in English and Arabic.

**Download:** grab `Fadi.Tools.exe` from the [latest release](../../releases/latest) and open it. The first start downloads the free engines it uses (FFmpeg, ImageMagick, yt-dlp, qpdf).

## What it does
- **Convert** images, audio, video, documents and data files
- **Studio** — edit photos (text, background removal, crop, adjust), videos (CapCut-style timeline, timed text, transitions, music, auto captions, projects) and PDFs (combine, reorder, rotate, delete, split, sign, number, watermark, lock)
- **Compress** — smaller files, same resolution; one-click Discord / Email / WhatsApp sizes; uses your graphics card when it can
- **Rename** many files at once, with undo
- **Download** from YouTube and 1,000+ sites
- **File inspector**, with **Reverse engineer** inside it, for the curious

Settings and engines live in `%LOCALAPPDATA%\Fadi Tools` (before 1.4.0: `%LOCALAPPDATA%\FileConverter`, moved automatically). A portable `.zip` can be made from Settings.

## Building
`bash build.sh` on Linux/macOS (Mono `mcs`), or `powershell -ExecutionPolicy Bypass -File build.ps1` on Windows (the C# compiler from Visual Studio / Build Tools, or the one inside Windows). .NET Framework 4.5 WinForms. Output: `build/Fadi Tools.exe` (the one file to publish).

## Checking it
- `"Fadi Tools.exe" /selftest <folder>` opens every page in light, dark and Arabic, saves a picture of each into the folder, checks the translations and a few features (copy-text bracket fix, searchable-PDF text layer), and exits with 0 when all is well (`selftest.log` says what failed).
- GitHub runs the same build and self-test on Windows for every push and pull request (`.github/workflows/build.yml`); the pictures and the built exe are in each run's **Artifacts**.
- **Ctrl+Alt+Shift+S** in the app (or `"Fadi Tools.exe" /screenshots <folder>`) makes fresh pictures of every page with made-up example files for the project page (`screenshots/`). Both runs start from fresh settings that are never saved, so your own settings stay as they are.

## Releasing a new version
1. Raise `Version` in `src/Shell.cs` and `src/AssemblyInfo.cs` (1.0.0 → 1.0.1 for fixes, 1.1.0 for new features). The app only offers an update when the release tag is higher than its own version.
2. Write `release/notes-<version>.md` with **both** parts:
   - a short description of the app (what it is, how to download and open it)
   - a changelog with **Added**, **Improved**, **Fixed** (and **Removed** when something goes) sections
3. On GitHub: Releases → Draft a new release → tag `<version>` → paste the notes → attach `Fadi Tools.exe` (used by the in-app updater) and the portable `.zip` → Publish.
4. winget (Windows' package catalog): `python release/winget.py` writes the manifests for the version into `release/winget/<version>/` (it hashes `build/Fadi Tools.exe`, so run it on exactly the file you uploaded, or use `--from-github`). The first time, send them with `wingetcreate submit --token <token> release/winget/<version>`; after that, `wingetcreate update RockeyUS.FadiTools --version <version> --urls https://github.com/RockeyUS/Fadi-Tools/releases/download/<version>/Fadi.Tools.exe --submit --token <token>`. Once accepted, anyone can run `winget install RockeyUS.FadiTools`.
