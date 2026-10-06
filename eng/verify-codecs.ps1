param(
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [switch]$SkipNativeBridge
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$encoderDir = Join-Path $repo "external/encoders/$Platform"
$nativeDir = Join-Path $repo "native/HdrImageViewer.Native/build/$Platform/Release"
$manifestPath = Join-Path $encoderDir 'codec-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Pinned codec bundle missing. Run: python eng/build-codecs.py --apply'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1) { throw 'Unsupported codec manifest schema.' }
$expectedLock = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'codecs.lock.json')).Hash
$bundledLock = (Get-FileHash -LiteralPath (Join-Path $encoderDir 'codecs.lock.json')).Hash
if ($expectedLock -ne $bundledLock) { throw 'Bundled codec lock differs from eng/codecs.lock.json. Rebuild the bundle.' }
$codecLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'codecs.lock.json') -Raw | ConvertFrom-Json
$ultraHdr = $codecLock.sources | Where-Object name -eq 'libultrahdr'
$groups = @{ encoders = $encoderDir }
if (-not $SkipNativeBridge) { $groups.native = $nativeDir }
$count = 0
foreach ($group in $groups.Keys) {
    foreach ($file in $manifest.files.$group.PSObject.Properties) {
        if ([IO.Path]::GetFileName($file.Name) -ne $file.Name) { throw 'Invalid manifest filename.' }
        $path = Join-Path $groups[$group] $file.Name
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing $group/$($file.Name)" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Value) {
            throw "Checksum mismatch: $group/$($file.Name)"
        }
        $count++
    }
}
# Check launchability without finding missing DLLs in a developer's MSYS2 PATH.
$previousPath = $env:PATH
try {
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    $checks = @(
        @{ Tool = 'ultrahdr_app.exe'; Argument = '--help'; Version = "v$($ultraHdr.version)" },
        @{ Tool = 'cjxl.exe'; Argument = '--version'; Version = '0.12.0' },
        @{ Tool = 'avifenc.exe'; Argument = '--version'; Version = '1.4.2' },
        @{ Tool = 'avifgainmaputil.exe'; Argument = 'help'; Version = '1.4.2' },
        @{ Tool = 'heif-enc.exe'; Argument = '--version'; Version = '1.23.5' }
    )
    foreach ($check in $checks) {
        $output = (& (Join-Path $encoderDir $check.Tool) $check.Argument 2>&1 | Out-String)
        if (-not $output.Contains($check.Version)) { throw "$($check.Tool) failed version/load check: $output" }
        Write-Host "$($check.Tool): $($check.Version) OK"
    }
} finally {
    $env:PATH = $previousPath
}
Write-Host "Verified $count runtime files against SHA256 manifest."
Write-Host "Ultra HDR source revision: $($ultraHdr.revision)"
