# Builds "build\Fadi Tools.exe" on Windows (build.sh does the same with Mono on Linux).
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# on GitHub, a failure is also shown as a note on the run (so it can be read without opening the log)
trap { if ($env:GITHUB_ACTIONS) { Write-Host ('::error::' + (($_ | Out-String) -replace "[`r`n]+", ' | ').Trim()) }; break }

python checklang.py
if ($LASTEXITCODE -ne 0) { throw 'A text key is listed twice in src\Lang.cs' }

# the C# compiler: the one that comes with Visual Studio / Build Tools, else the one inside Windows
$csc = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $csc = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
}
if (-not $csc) { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
Write-Host "Compiler: $csc"

# .NET Framework 4.x reference assemblies when installed, else the ones Windows runs
$refRoot = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework"
$ref = $null
if (Test-Path $refRoot) {
    $ref = Get-ChildItem $refRoot -Directory | Where-Object { $_.Name -match '^v4\.[5-8]' -and (Test-Path (Join-Path $_.FullName 'mscorlib.dll')) } |
           Sort-Object { [version]($_.Name.TrimStart('v')) } | Select-Object -Last 1
}
$libs = 'System.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'System.Core.dll', 'System.Xml.dll', 'Accessibility.dll'
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/out:build\FileConverter.exe', '/win32icon:assets\app.ico',
          '/resource:assets\logo.png,FileConverter.logo.png', '/resource:assets\logo_silver.png,FileConverter.logo_silver.png', '/resource:assets\logo_dark.png,FileConverter.logo_dark.png')
if ($ref) {
    Write-Host "References: $($ref.FullName)"
    $cscArgs += '/noconfig', '/nostdlib+', "/r:$(Join-Path $ref.FullName 'mscorlib.dll')"
    foreach ($l in $libs) { $cscArgs += "/r:$(Join-Path $ref.FullName $l)" }
} else {
    foreach ($l in $libs) { $cscArgs += "/r:$l" }
}
$cscArgs += 'src\*.cs'

New-Item -ItemType Directory -Force build | Out-Null
$log = & $csc @cscArgs 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Host $_ }
if ($code -ne 0)
{
    if ($env:GITHUB_ACTIONS) { $log | Where-Object { "$_" -match 'error' } | Select-Object -First 25 | ForEach-Object { Write-Host "::error::$_" } }
    throw "The build failed ($code)"
}
Copy-Item build\FileConverter.exe 'build\Fadi Tools.exe' -Force
Write-Host 'Built build\Fadi Tools.exe'
