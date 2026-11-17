param(
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$shaderDirectory = Join-Path $repoRoot 'Rendering\Shaders'
$sourcePath = Join-Path $shaderDirectory 'GainMap.hlsl'
$kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Shader source not found: $sourcePath"
}

$fxc = $null
if (-not [string]::IsNullOrWhiteSpace($env:WindowsSdkVerBinPath)) {
    foreach ($candidate in @(
        (Join-Path $env:WindowsSdkVerBinPath 'x64\fxc.exe'),
        (Join-Path $env:WindowsSdkVerBinPath 'fxc.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            $fxc = Get-Item -LiteralPath $candidate
            break
        }
    }
}

if ($null -eq $fxc -and (Test-Path -LiteralPath $kitsBin)) {
    $fxc = Get-ChildItem -Path $kitsBin -Filter fxc.exe -File -Recurse |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object {
            $version = $null
            if ([Version]::TryParse($_.Directory.Parent.Name, [ref]$version)) {
                $version
            }
            else {
                [Version]'0.0'
            }
        } -Descending |
        Select-Object -First 1
}

if ($null -eq $fxc) {
    throw "fxc.exe was not found via WindowsSdkVerBinPath or under $kitsBin"
}

$shaders = [System.Collections.Generic.List[hashtable]]@(
    @{
        EntryPoint = 'VSMain'
        Profile = 'vs_5_0'
        Output = 'GainMap.VS.cso'
    },
    @{
        EntryPoint = 'PSMain'
        Profile = 'ps_5_0'
        Output = 'GainMap.PS.cso'
    },
    @{
        EntryPoint = 'BaseImagePSMain'
        Profile = 'ps_5_0'
        Output = 'BaseImage.PS.cso'
    },
    @{
        EntryPoint = 'LinearizeSourcePSMain'
        Profile = 'ps_5_0'
        Output = 'Linearize.PS.cso'
    }
)

if (Select-String -Pattern '\bComputeAnalysisCSMain\b' -Quiet $sourcePath) {
    $shaders.Add(@{
        EntryPoint = 'ComputeAnalysisCSMain'
        Profile = 'cs_5_0'
        Output = 'Analysis.CS.cso'
    })
}

$tempDirectory = $null
if ($VerifyOnly) {
    $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("hdriv-shaders-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null
}

try {
    foreach ($shader in $shaders) {
        $targetPath = Join-Path $shaderDirectory $shader.Output
        $outputPath = if ($VerifyOnly) { Join-Path $tempDirectory $shader.Output } else { $targetPath }
        $arguments = @(
            '/nologo',
            '/WX',
            '/O3',
            '/Qstrip_debug',
            '/Qstrip_reflect',
            '/T', $shader.Profile,
            '/E', $shader.EntryPoint,
            '/Fo', $outputPath,
            $sourcePath
        )

        & $fxc.FullName @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Shader compilation failed for $($shader.EntryPoint) ($($shader.Profile))"
        }

        if ($VerifyOnly) {
            if (-not (Test-Path -LiteralPath $targetPath)) {
                throw "Committed shader bytecode missing: $targetPath. Run .\eng\CompileShaders.ps1 to generate it."
            }

            $expectedBytes = [System.IO.File]::ReadAllBytes($targetPath)
            $actualBytes = [System.IO.File]::ReadAllBytes($outputPath)
            if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$expectedBytes, [byte[]]$actualBytes)) {
                throw "Shader bytecode mismatch for $($shader.Output). Run .\eng\CompileShaders.ps1 to update committed .cso files."
            }

            Write-Host "Verified $($shader.EntryPoint) -> $targetPath"
        }
        else {
            Write-Host "Compiled $($shader.EntryPoint) -> $outputPath"
        }
    }
}
finally {
    if ($null -ne $tempDirectory -and (Test-Path -LiteralPath $tempDirectory)) {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force
    }
}
