# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Fadi Tools.exe is built from the source code in this repository by GitHub Actions ([release.yml](.github/workflows/release.yml)), tested, and then signed. Only files built from this repository are signed. The programs Fadi Tools uses (FFmpeg, ImageMagick, yt-dlp and the others) are not signed by this project; they come from their own makers.

## Team roles

- **Committers and reviewers:** [RockeyUS](https://github.com/RockeyUS)
- **Approvers:** [RockeyUS](https://github.com/RockeyUS)

Every release is approved by hand before it is signed.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

What Fadi Tools connects to, and when:

- **Updates:** once a day it asks GitHub (api.github.com) whether a newer version of Fadi Tools exists. Turn this off in Settings.
- **Engines and add-ons:** when you use a feature that needs a free program it doesn't have yet (for example FFmpeg, yt-dlp, auto captions or the background remover), it downloads that program from its official download page or from GitHub.
- **Downloads:** the links you paste on the Download page are fetched from the sites they point to.
- **Look up online** in the File inspector opens VirusTotal in your browser with the file's SHA-256 fingerprint. The file itself is never sent. **Show on map** opens OpenStreetMap at a photo's location. Both happen only when you click them.

Fadi Tools has no accounts, no ads and no tracking, and it doesn't send your files anywhere.
