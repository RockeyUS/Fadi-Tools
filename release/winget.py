#!/usr/bin/env python3
"""Writes the winget manifests for the current version into release/winget/<version>/.

    python release/winget.py                       # hashes build/Fadi Tools.exe (upload exactly this file as Fadi.Tools.exe)
    python release/winget.py --exe "C:/path/Fadi Tools.exe"
    python release/winget.py --from-github         # hashes the Fadi.Tools.exe already published in the release

Then send them to Microsoft's winget catalog (a pull request to microsoft/winget-pkgs) with Microsoft's own tool:

    winget install wingetcreate
    wingetcreate submit --token <a GitHub token with "public_repo"> release/winget/<version>

After the first version is accepted, later versions only need:

    wingetcreate update RockeyUS.FadiTools --version <version> --urls https://github.com/RockeyUS/Fadi-Tools/releases/download/<version>/Fadi.Tools.exe --submit --token <token>

People can then install the app with:  winget install RockeyUS.FadiTools
"""
import argparse, datetime, hashlib, os, re, sys, urllib.request

ID = "RockeyUS.FadiTools"
REPO = "RockeyUS/Fadi-Tools"
SCHEMA = "1.6.0"


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=os.path.join(root, "build", "Fadi Tools.exe"))
    ap.add_argument("--from-github", action="store_true", help="hash the published Fadi.Tools.exe instead of a local file")
    ap.add_argument("--date", default=datetime.date.today().isoformat())
    a = ap.parse_args()

    ver = re.search(r'Version = "([0-9.]+)"', open(os.path.join(root, "src", "Shell.cs"), encoding="utf-8").read()).group(1)
    url = "https://github.com/%s/releases/download/%s/Fadi.Tools.exe" % (REPO, ver)
    if a.from_github:
        with urllib.request.urlopen(url) as r:
            data = r.read()
    else:
        if not os.path.exists(a.exe):
            sys.exit("Not found: " + a.exe)
        data = open(a.exe, "rb").read()
    sha = hashlib.sha256(data).hexdigest().upper()

    out = os.path.join(here, "winget", ver)
    os.makedirs(out, exist_ok=True)
    head = "# yaml-language-server: $schema=https://aka.ms/winget-manifest.%s.%s.schema.json\n\n"

    files = {
        ID + ".yaml": head % ("version", SCHEMA) +
        "PackageIdentifier: %s\nPackageVersion: %s\nDefaultLocale: en-US\nManifestType: version\nManifestVersion: %s\n" % (ID, ver, SCHEMA),

        ID + ".installer.yaml": head % ("installer", SCHEMA) +
        "PackageIdentifier: %s\nPackageVersion: %s\n" % (ID, ver) +
        "MinimumOSVersion: 10.0.0.0\n"
        "InstallerType: portable\n"
        "Commands:\n- fadi-tools\n"
        "ReleaseDate: %s\n" % a.date +
        "Installers:\n"
        "- Architecture: neutral\n"
        "  InstallerUrl: %s\n"
        "  InstallerSha256: %s\n" % (url, sha) +
        "ManifestType: installer\nManifestVersion: %s\n" % SCHEMA,

        ID + ".locale.en-US.yaml": head % ("defaultLocale", SCHEMA) +
        "PackageIdentifier: %s\nPackageVersion: %s\nPackageLocale: en-US\n" % (ID, ver) +
        "Publisher: RockeyUS\n"
        "PublisherUrl: https://github.com/RockeyUS\n"
        "PublisherSupportUrl: https://github.com/%s/issues\n" % REPO +
        "Author: Fadi\n"
        "PackageName: Fadi Tools\n"
        "PackageUrl: https://github.com/%s\n" % REPO +
        "License: Freeware\n"
        "Copyright: Fadi\n"
        "ShortDescription: Convert, compress, edit and download files - free, offline, in English and Arabic.\n"
        "Description: |-\n"
        "  An all-in-one Windows app. Convert pictures, audio, video, documents and data files; compress videos and photos\n"
        "  (with one-click sizes for Discord and email); edit photos, videos and PDFs in the Studio; copy text from pictures\n"
        "  and make scanned PDFs searchable; rename many files at once; find duplicate files; download from YouTube and\n"
        "  1,000+ sites. Works offline, in English and Arabic.\n"
        "Moniker: fadi-tools\n"
        "Tags:\n- converter\n- compressor\n- video-editor\n- pdf\n- ocr\n- downloader\n- duplicate-finder\n- arabic\n"
        "ReleaseNotesUrl: https://github.com/%s/releases/tag/%s\n" % (REPO, ver) +
        "ManifestType: defaultLocale\nManifestVersion: %s\n" % SCHEMA,

        ID + ".locale.ar-SA.yaml": head % ("locale", SCHEMA) +
        "PackageIdentifier: %s\nPackageVersion: %s\nPackageLocale: ar-SA\n" % (ID, ver) +
        "PackageName: Fadi Tools\n"
        "ShortDescription: حوّل الملفات واضغطها وحرّرها ونزّلها — مجانًا وبدون إنترنت، بالعربية والإنجليزية.\n"
        "ManifestType: locale\nManifestVersion: %s\n" % SCHEMA,
    }
    for name, text in files.items():
        with open(os.path.join(out, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    print("winget manifests for %s written to %s" % (ver, out))
    print("  installer: %s" % url)
    print("  sha256:    %s" % sha)


if __name__ == "__main__":
    main()
