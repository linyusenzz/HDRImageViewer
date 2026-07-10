param(
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [switch]$SkipNative,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj = Join-Path $repo 'HdrImageViewer.csproj'
$appPackagesDir = Join-Path $repo 'AppPackages'

function Find-SignTool {
    $tool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) {
        throw 'signtool.exe was not found. Install the Windows 10/11 SDK or omit -PfxPath to leave the MSIX unsigned.'
    }

    return $tool.FullName
}

Push-Location $repo
try {
    if (-not $NoClean) {
        Remove-Item -LiteralPath $appPackagesDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not $SkipNative) {
        Write-Host '== Native bridge =='
        & (Join-Path $PSScriptRoot 'build-native.ps1') -Platforms @($Platform) -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "native build failed (exit $LASTEXITCODE)" }
    }

    Write-Host ''
    Write-Host '== Publish MSIX =='
    dotnet publish $csproj `
        -c $Configuration `
        -r "win-$Platform" `
        -p:Platform=$Platform `
        -p:RuntimeIdentifiers=win-x64 `
        -p:AppxBundlePlatforms=x64 `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxPackageDir='AppPackages/' `
        -p:AppxBundle=Never `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=false `
        -p:PublishTrimmed=false `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    $msix = Get-ChildItem -Path (Join-Path $appPackagesDir "HdrImageViewer_*_$($Platform)_Test") -Filter "HdrImageViewer_*_$($Platform).msix" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $msix) {
        throw "MSIX package was not produced under $appPackagesDir."
    }

    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        $resolvedPfx = (Resolve-Path $PfxPath).Path
        $signtool = Find-SignTool
        Write-Host ''
        Write-Host '== Sign MSIX =='
        if ([string]::IsNullOrEmpty($PfxPassword)) {
            & $signtool sign /fd SHA256 /f $resolvedPfx $msix.FullName
        }
        else {
            & $signtool sign /fd SHA256 /f $resolvedPfx /p $PfxPassword $msix.FullName
        }

        if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)" }
    }
    else {
        Write-Warning 'MSIX was generated but not signed. Pass -PfxPath to sign a sideload package.'
    }

    $size = [math]::Round($msix.Length / 1MB, 1)
    Write-Host ''
    Write-Host "MSIX: $($msix.FullName)"
    Write-Host "Size: $size MB"
}
finally {
    Pop-Location
}
