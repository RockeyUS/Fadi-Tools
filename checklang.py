"""Stops the build when a text key is listed twice in src/Lang.cs (the app would fail to start)."""
import collections, os, re, sys
here = os.path.dirname(os.path.abspath(__file__))
s = open(os.path.join(here, 'src', 'Lang.cs'), encoding='utf-8').read()
keys = re.findall(r'\{ "([^"]+)", new\[\]', s)
d = [k for k, c in collections.Counter(keys).items() if c > 1]
if d:
    print("DUPLICATE KEYS:", d)
    sys.exit(1)
