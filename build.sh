#!/bin/sh
# Builds build/Fadi Tools.exe on Linux. build.ps1 does the same on Windows.
# Uses Microsoft's C# compiler (Roslyn, run with Mono) when it's there: its exe has the same shape as one built
# with Visual Studio (a Windows manifest, a real build date, the usual sections), which antivirus "machine learning"
# checks trust more than Mono's own compiler output. Point CSC at Roslyn's csc.exe, or it's looked for in the usual places.
cd "$(dirname "$0")" && python3 checklang.py || exit 1
mkdir -p build
RES="-resource:assets/logo.png,FileConverter.logo.png -resource:assets/logo_dark.png,FileConverter.logo_dark.png -resource:assets/logo_small.png,FileConverter.logo_small.png -resource:assets/logo_dark_small.png,FileConverter.logo_dark_small.png"
LIBS="System.dll System.Windows.Forms.dll System.Drawing.dll System.IO.Compression.dll System.IO.Compression.FileSystem.dll System.Core.dll System.Xml.dll Accessibility.dll"
for c in "$CSC" "$HOME/.nuget/packages/microsoft.net.compilers/3.9.0/tools/csc.exe" /tmp/claude-0/roslyn/Microsoft.Net.Compilers/3.9.0/tools/csc.exe /tmp/claude-0/roslyn/Microsoft.Net.Compilers/3.9.0/csc.exe; do
  if [ -n "$c" ] && [ -f "$c" ]; then ROSLYN="$c"; break; fi
done
if [ -n "$ROSLYN" ] && [ -z "$USE_MCS" ]; then
  R=/usr/lib/mono/4.5-api
  REFS="-r:$R/mscorlib.dll"; for l in $LIBS; do REFS="$REFS -r:$R/$l"; done
  mono "$ROSLYN" -nologo -langversion:5 -target:winexe -optimize+ -platform:anycpu -out:build/FileConverter.exe -win32icon:assets/app.ico $RES \
    -noconfig -nostdlib+ $REFS src/*.cs || exit 1
  echo "Compilation succeeded (Roslyn)"
else
  REFS=""; for l in $LIBS; do REFS="$REFS -r:$l"; done
  mcs -sdk:4.5 -target:winexe -optimize+ -platform:anycpu -out:build/FileConverter.exe -win32icon:assets/app.ico $RES $REFS src/*.cs || exit 1
fi
cp build/FileConverter.exe "build/Fadi Tools.exe"
