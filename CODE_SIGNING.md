# Privacy and builds

Fadi Tools isn't code-signed yet. Free signing from the [SignPath Foundation](https://signpath.org) is for projects that are already widely known, so Fadi Tools will apply again once more people use it.

Every release of Fadi Tools.exe is built from the source code in this repository by GitHub Actions ([release.yml](.github/workflows/release.yml)) and tested before it's published. The programs Fadi Tools uses (FFmpeg, ImageMagick, yt-dlp and the others) come from their own makers.

## Team roles

- **Committers and reviewers:** [RockeyUS](https://github.com/RockeyUS)
- **Approvers:** [RockeyUS](https://github.com/RockeyUS)

Every release is checked by hand before it's published.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

What Fadi Tools connects to, and when:

- **Updates:** once a day it asks GitHub (api.github.com) whether a newer version of Fadi Tools exists. Turn this off in Settings.
- **Engines and add-ons:** when you use a feature that needs a free program it doesn't have yet (for example FFmpeg, yt-dlp, auto captions or the background remover), it downloads that program from its official download page or from GitHub.
- **Downloads:** the links you paste on the Download page are fetched from the sites they point to.
- **Look up online** in the File inspector opens VirusTotal in your browser with the file's SHA-256 fingerprint. The file itself is never sent. **Show on map** opens OpenStreetMap at a photo's location. Both happen only when you click them.

Fadi Tools has no accounts, no ads and no tracking, and it doesn't send your files anywhere.
