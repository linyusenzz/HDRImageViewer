param(
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$Version = '',
    [switch]$SkipTests,
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj = Join-Path $repo 'HdrImageViewer.csproj'
$testProject = Join-Path $repo 'tests\HdrImageViewer.Tests\HdrImageViewer.Tests.csproj'
$artifactsDir = Join-Path $repo 'artifacts'
$publishDir = Join-Path $artifactsDir "portable\win-$Platform"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -Raw -Path $csproj
    $Version = $project.Project.PropertyGroup.Version | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version was not provided and could not be read from HdrImageViewer.csproj.'
}

New-Item -ItemType Directory -Force $artifactsDir | Out-Null
Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue

Push-Location $repo
try {
    Write-Host "== Restore =="
    dotnet restore $csproj -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)" }

    if (-not $SkipTests) {
        Write-Host ''
        Write-Host '== Test =='
        dotnet test $testProject -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (exit $LASTEXITCODE)" }
    }

    if (-not $SkipNative) {
        Write-Host ''
        Write-Host '== Native bridge =='
        & (Join-Path $PSScriptRoot 'build-native.ps1') -Platforms @($Platform) -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "native build failed (exit $LASTEXITCODE)" }
    }

    Write-Host ''
    Write-Host '== Publish portable =='
    # Pass the version through so the published exe's assembly/file version
    # matches the zip name even when the tag and csproj <Version> diverge.
    dotnet publish $csproj `
        -c $Configuration `
        -r "win-$Platform" `
        -p:Platform=$Platform `
        -p:PortableBuild=true `
        -p:Version=$Version `
        -p:PublishDir="$publishDir\" `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    $exe = Join-Path $publishDir 'HdrImageViewer.exe'
    if (-not (Test-Path $exe)) {
        throw "Portable publish did not produce HdrImageViewer.exe: $exe"
    }

    $requiredRuntimeFiles = @(
        'App.xbf',
        'MainWindow.xbf',
        'HdrImageViewer.pri',
        'Pages\HomePage.xbf',
        'Pages\SettingsPage.xbf',
        'Assets\AppIcon.ico'
    )
    $missingRuntimeFiles = $requiredRuntimeFiles |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $publishDir $_)) }
    if ($missingRuntimeFiles) {
        throw "Portable publish is missing WinUI runtime resources: $($missingRuntimeFiles -join ', ')"
    }

    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $publishDir 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $publishDir 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $publishDir 'THIRD_PARTY_NOTICES.md') -Force

    $zip = Join-Path $artifactsDir "HdrImageViewer-$Version-win-$Platform-portable.zip"
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal -Force

    $zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Portable zip: $zip"
    Write-Host "Size: $zipSize MB"

    if ($env:GITHUB_OUTPUT) {
        "zip_path=$zip" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "zip_name=$(Split-Path $zip -Leaf)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    }
}
finally {
    Pop-Location
}
