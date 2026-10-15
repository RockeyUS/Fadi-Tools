import re,collections,sys
s=open('/home/claude/FC3/src/Lang.cs',encoding='utf-8').read()
keys=re.findall(r'\{ "([^"]+)", new\[\]',s)
d=[k for k,c in collections.Counter(keys).items() if c>1]
if d: print("DUPLICATE KEYS:",d); sys.exit(1)
