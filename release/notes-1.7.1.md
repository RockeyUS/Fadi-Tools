## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, find duplicates, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.7.1

### Video studio: plays straight away
- **No more "Getting the preview ready".** Play starts at once, every time, even right after an edit. The picture and the sound are put together live as it plays, instead of making a preview video first.
- **Changes while it plays.** Volume, speed, fades, mute, keyframes, texts and overlays apply while the video plays: you hear and see the change right away.
- **Click the picture** to pause or play.
- **Click the timeline** (the ruler, an empty spot or a clip) and the playhead jumps there. While playing, it plays on from that spot.
- **Preview volume.** A volume slider on the far right of the play controls sets how loud the preview is (it doesn't change the video). The speaker button mutes it.
- **Smoother editing.** Trimming a clip by its edge, dragging things on the picture and moving any slider no longer stutters: the picture keeps up as you drag.

### Captions
- **The whole video gets captions.** Auto captions no longer stop after the first few words. Whisper used to skip stretches it wasn't sure about (often speech with music behind it) and could get stuck on what it had heard before.
- **Each word lights up when it's said.** Word highlight now uses the time Whisper hears each word, instead of spreading the words evenly over the line.
- **Lines follow the speech.** A new line starts after a pause or at the end of a sentence, and long lines are split.
- **Arabic reads from right to left**, in the preview and in the exported video, also with Word highlight and Bold pop.

### Arabic fonts and add-ons
- **Get free Arabic fonts** from the font menu in the video studio (texts and captions) and the photo studio. Cairo, Tajawal, Amiri, Reem Kufi, Aref Ruqaa and more are added, and captions use Cairo once they're in.
- **Settings → Add-ons** lists everything Fadi Tools downloaded: Arabic fonts, auto captions, the background remover, the photo upscaler, the PDF page tools, yt-dlp, LibreOffice and Node.js. Each one shows where it's installed and how big it is, with a button to open its folder and an **Uninstall** button. FFmpeg and ImageMagick are listed too; they can't be uninstalled because Fadi Tools needs them.

### Fixed
- **Exporting a video** with FFmpeg 8 failed with "Error splitting the argument list: Option not found". Fadi Tools now works with FFmpeg 8 and older versions.
