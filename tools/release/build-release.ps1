# Builds the portable zip and the installer into artifacts\release\out.
# Run from anywhere:  pwsh tools\release\build-release.ps1
# Builds under artifacts\release, so the fixed user build under src\OverTranslate\bin is not touched.
param(
    [string]$Iscc = "D:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $root "src\OverTranslate\OverTranslate.csproj"
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No Version in $project" }
if (-not (Test-Path $Iscc)) { throw "Inno Setup compiler not found: $Iscc" }

$work = Join-Path $root "artifacts\release"
$stage = Join-Path $work "stage\TextTack"
$out = Join-Path $work "out"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# ReadyToRun: the app's own code ships precompiled, so a slow machine does not spend its first
# seconds compiling it before the tray icon appears. Same code, a few MB larger.
# LocalDiagnostics=false: the local diagnostics logging (src\OverTranslate\Services\LocalDiagnostics) is for the
# developer's own machine and never ships.
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:LocalDiagnostics=false `
    --source https://api.nuget.org/v3/index.json --artifacts-path (Join-Path $work "build") -o $stage -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Nothing personal or stray may reach a public package.
$bad = Get-ChildItem $stage -Recurse -Force | Where-Object {
    $_.Name -in @("profile-thai", "appsettings.json") -or $_.Name -like "appsettings.before-*" -or $_.Extension -eq ".pdb"
}
if ($bad) { throw "Refusing to package: $($bad.FullName -join ', ')" }
foreach ($required in "TextTack.exe", "hostfxr.dll", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt",
    "icons\app.ico", "ocrmodels\onnx\thai\rec.onnx", "ocrmodels\onnx\thai\dict.txt", "ocrmodels\onnx\thai\provenance.json") {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "Missing from package: $required" }
}

# Every local diagnostics log line starts with a marker (ForegroundTrail.Marker, spelled by code point to keep
# this file ASCII); finding it in the assembly means the diagnostics were compiled in. Searched at
# both byte alignments, since the string heap does not keep UTF-16 text on even offsets.
$marker = -join [char[]](0x5B, 0x672C, 0x6A5F, 0x6AA2, 0x6E2C, 0x5D)
$dll = [System.IO.File]::ReadAllBytes((Join-Path $stage "TextTack.dll"))
$utf16 = [System.Text.Encoding]::Unicode
if ($utf16.GetString($dll).Contains($marker) -or $utf16.GetString($dll, 1, $dll.Length - 1).Contains($marker)) {
    throw "Refusing to package: TextTack.dll still contains the local diagnostics"
}

$zip = Join-Path $out "TextTack-$version-win-x64-Portable.zip"
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

& $Iscc "/DAppVersion=$version" "/DSourceDir=$stage" "/O$out" "/Q" (Join-Path $PSScriptRoot "TextTack.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-ChildItem $out -File | Where-Object Name -ne "SHA256SUMS.txt" | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ascii

Get-ChildItem $out -File | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
