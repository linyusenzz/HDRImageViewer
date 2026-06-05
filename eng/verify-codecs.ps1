param(
    [ValidateSet('x64', 'x86', 'ARM64')]
    [string]$Platform = 'x64',
    [switch]$RepairUltraHdr,
    [switch]$SkipNativeBridge
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$encoderDir = Join-Path $repo "external\encoders\$Platform"
$dependencyCacheDir = Join-Path $repo 'external\_deps'
$nativeBridgeDir = Join-Path $repo "native\HdrImageViewer.Native\build\$Platform\Release"

$expectedEncoderFiles = @(
    'aom.dll',
    'avif.dll',
    'avifdec.exe',
    'avifenc.exe',
    'brotlicommon.dll',
    'brotlidec.dll',
    'brotlienc.dll',
    'cjxl.exe',
    'djxl.exe',
    'heif-enc.exe',
    'heif.dll',
    'hwy.dll',
    'jpeg62.dll',
    'jxl.dll',
    'jxl_cms.dll',
    'jxl_threads.dll',
    'jxlinfo.exe',
    'lcms2-2.dll',
    'libde265.dll',
    'libpng16.dll',
    'libx265.dll',
    'libyuv.dll',
    'MSVCP140.dll',
    'ultrahdr_app.exe',
    'VCRUNTIME140.dll',
    'VCRUNTIME140_1.dll',
    'z.dll'
)

$expectedNativeBridgeFiles = @(
    'HdrImageViewer.Native.dll',
    'deflate.dll',
    'Iex-3_4.dll',
    'IlmThread-3_4.dll',
    'Imath-3_2.dll',
    'OpenEXR-3_4.dll',
    'OpenEXRCore-3_4.dll',
    'openjph.0.27.dll'
)

if ($RepairUltraHdr) {
    $ultraHdrTarget = Join-Path $encoderDir 'ultrahdr_app.exe'
    $ultraHdrSource = @(
        (Join-Path $dependencyCacheDir 'libultrahdr\build\Release\ultrahdr_app.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not (Test-Path $ultraHdrTarget) -and $ultraHdrSource) {
        New-Item -ItemType Directory -Force $encoderDir | Out-Null
        Copy-Item -LiteralPath $ultraHdrSource -Destination $ultraHdrTarget -Force
        Write-Host "Copied ultrahdr_app.exe to $ultraHdrTarget"
    }
}

function Test-ExpectedFiles {
    param(
        [string]$Root,
        [string[]]$Files,
        [string]$Group
    )

    foreach ($file in $Files) {
        $path = Join-Path $Root $file
        [PSCustomObject]@{
            Group = $Group
            File = $file
            Present = Test-Path $path
            Path = $path
        }
    }
}

$results = @()
$results += Test-ExpectedFiles -Root $encoderDir -Files $expectedEncoderFiles -Group "encoders\$Platform"
if (-not $SkipNativeBridge) {
    $results += Test-ExpectedFiles -Root $nativeBridgeDir -Files $expectedNativeBridgeFiles -Group "native-bridge\$Platform"
}

$results | Format-Table Group, File, Present, Path -AutoSize

$missing = @($results | Where-Object { -not $_.Present })
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Warning "Missing $($missing.Count) codec/native dependency file(s)."
    Write-Host "Bundled encoder source of truth: $encoderDir"
    Write-Host "Project-local dependency cache: $dependencyCacheDir"
    Write-Host "Native bridge build output:       $nativeBridgeDir"
    Write-Host ''
    Write-Host 'Notes:'
    Write-Host '- Do not overwrite libx265.dll from a stock MSYS2/vcpkg build unless you intend to drop HEIC 10-bit support.'
    Write-Host '- Use -RepairUltraHdr only to copy ultrahdr_app.exe from external\_deps\libultrahdr\build\Release when that local build exists.'
    exit 1
}

Write-Host ''
Write-Host "All expected $Platform codec/native dependency files are present."
