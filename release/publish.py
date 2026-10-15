#!/usr/bin/env python3
"""Publishes a Fadi Tools release on GitHub so the app's "Check for updates" finds it.

    GITHUB_TOKEN=... python3 publish.py --repo RockeyUS/fadi-tools --exe "../build/Fadi Tools.exe" [--zip portable.zip]

The version comes from src/Shell.cs and the notes from release/notes-<version>.md.
The app installs the first .exe it finds in the newest release, so sign the .exe first (sign.ps1) if you have a certificate.
"""
import argparse, json, os, re, sys, urllib.request

def api(method, url, token, data=None, ctype="application/json"):
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + token)
    req.add_header("Accept", "application/vnd.github+json")
    if data is not None: req.add_header("Content-Type", ctype)
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read().decode() or "{}")

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="RockeyUS/fadi-tools")
    ap.add_argument("--exe", default=os.path.join(here, "..", "build", "Fadi Tools.exe"))
    ap.add_argument("--zip", default=None)
    ap.add_argument("--draft", action="store_true")
    a = ap.parse_args()
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if not token: sys.exit("Set GITHUB_TOKEN to a token that can write to " + a.repo)
    ver = re.search(r'Version = "([0-9.]+)"', open(os.path.join(here, "..", "src", "Shell.cs"), encoding="utf-8").read()).group(1)
    notes_file = os.path.join(here, "notes-%s.md" % ver)
    notes = open(notes_file, encoding="utf-8").read() if os.path.exists(notes_file) else "Fadi Tools " + ver
    rel = api("POST", "https://api.github.com/repos/%s/releases" % a.repo, token,
              json.dumps({"tag_name": "v" + ver, "name": "Fadi Tools " + ver, "body": notes, "draft": a.draft}).encode())
    upload = rel["upload_url"].split("{")[0]
    for path, name, ctype in [(a.exe, "Fadi.Tools.exe", "application/vnd.microsoft.portable-executable"),
                              (a.zip, "Fadi.Tools.%s.portable.zip" % ver, "application/zip")]:
        if not path: continue
        data = open(path, "rb").read()
        api("POST", upload + "?name=" + name, token, data, ctype)
        print("uploaded", name, len(data), "bytes")
    print("published", rel.get("html_url"))

if __name__ == "__main__":
    main()
