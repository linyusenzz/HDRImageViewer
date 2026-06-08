# Build And Packaging

This document records the current local build, dependency, package, and sync layout. Codec behavior details live in `docs/CODECS_AND_FORMATS.md`.

## Current Baseline

- Project: `HdrImageViewer.csproj`.
- Target: `net10.0-windows10.0.26100.0`.
- Current project/MSIX version: `1.0.17.0`.
- Current maintained native dependency platform: x64.
- Current local package output:

```text
AppPackages\HdrImageViewer_1.0.17.0_x64_Test
```

## Local Build

Build:

```powershell
dotnet build .\HdrImageViewer.csproj -p:Platform=x64
```

Run:

```powershell
dotnet run --project .\HdrImageViewer.csproj -p:Platform=x64 --no-build
```

Launch verification should use `dotnet run`; directly starting the built exe can produce misleading Windows App SDK/package-identity failures.

## Portable Zip

Build a local portable zip:

```powershell
.\eng\publish-portable.ps1 -Version 1.0.17.0 -Platform x64
```

If `-Version` is omitted, `eng\publish-portable.ps1` reads the version from `HdrImageViewer.csproj`.

The GitHub workflow `.github/workflows/release-portable.yml` runs the same script for `v*` tags and uploads:

```text
artifacts\HdrImageViewer-<version>-win-x64-portable.zip
```

Portable builds are framework-dependent. They rely on .NET 10 Desktop Runtime and Windows App Runtime 2.x rather than copying incompatible vNext OS component DLLs.

## MSIX Package

Build and sign a local x64 MSIX package:

```powershell
.\eng\publish-msix.ps1 -Platform x64 -PfxPath .\dist\keys\HdrImageViewer-signing.pfx -PfxPassword <password>
```

The script generates and signs:

```text
AppPackages\HdrImageViewer_1.0.17.0_x64_Test\HdrImageViewer_1.0.17.0_x64.msix
```

If `-PfxPath` is omitted, the script still generates the MSIX but leaves it unsigned. A clean machine must trust the matching certificate before installing a self-signed sideload package.

## Project-Local Dependencies

The project folder is synced between machines, so local dependencies intentionally stay inside the repo folder while remaining ignored by git.

```text
external\
  encoders\
    x64\          # runtime/package codec and CLI tool source of truth
  _deps\          # synced source/build cache, not package input
```

Current `external\_deps` contents include:

- `build-sources`
- `libavif`
- `libheif`
- `libultrahdr`
- `x265-multilib`

Current size checkpoints:

- `external\encoders\x64`: root tool set plus isolated tool subdirectories such as `avifgainmaputil`.
- `external\_deps`: about `284.73 MB`.
- `native\HdrImageViewer.Native\build\x64\Release`: about `5.78 MB`.
- `AppPackages\HdrImageViewer_1.0.17.0_x64_Test`: about `109.8 MB`.

`bin/`, `obj/`, and `AppPackages/` are generated outputs and can be rebuilt. They are ignored by git.

## Bundled Runtime Copying

`HdrImageViewer.csproj` copies these local runtime inputs when present:

- `external\encoders\$(NativeDependencyPlatform)\**\*.*` to `encoders\$(NativeDependencyPlatform)`, preserving subdirectories.
- `native\HdrImageViewer.Native\build\$(NativeDependencyPlatform)\Release\*.dll` to the app output root when `HdrImageViewer.Native.dll` exists.

`NativeDependencyPlatform` is inferred from `Platform` / `RuntimeIdentifier`; only x64 is currently maintained.

Command-line codecs are process-isolated tools. If they are installed by the user and discovered from MSYS2 or PATH, the app is only invoking external programs and the release package is not redistributing those binaries. If files from `external\encoders\<arch>` are copied into a portable zip, MSIX, or Store package, then those copied files are part of the distributed package and need their own license notices and source-offer handling where applicable.

## Native Bridge

Build x64 OpenEXR bridge:

```powershell
.\eng\build-native.ps1 -Platforms x64 -Configuration Release
```

Manual CMake equivalent:

```powershell
cmake -S native/HdrImageViewer.Native -B native/HdrImageViewer.Native/build/x64 -A x64
cmake --build native/HdrImageViewer.Native/build/x64 --config Release
```

Expected output:

```text
native\HdrImageViewer.Native\build\x64\Release
```

If OpenEXR is not found, the native project can build a stub DLL that reports EXR decode as unavailable so the managed app still compiles.

## Dependency Verification

Run:

```powershell
.\eng\verify-codecs.ps1
```

The script checks:

- x64 bundled codec/tool files in `external\encoders\x64`.
- x64 isolated AVIF gain-map tool files in `external\encoders\x64\avifgainmaputil`.
- x64 `HdrImageViewer.Native` OpenEXR bridge runtime files.

Repair Ultra HDR CLI from a local libultrahdr build:

```powershell
.\eng\verify-codecs.ps1 -RepairUltraHdr
```

Repair source:

```text
external\_deps\libultrahdr\build\Release\ultrahdr_app.exe
```

## Optional MSYS2 Fallback

MSYS2 UCRT64 remains useful as a fallback/source on the development machine:

```powershell
winget install --id MSYS2.MSYS2 --source winget
C:\msys64\usr\bin\pacman.exe -Syu --noconfirm
C:\msys64\usr\bin\pacman.exe -S --needed --noconfirm mingw-w64-ucrt-x86_64-libjxl mingw-w64-ucrt-x86_64-libavif mingw-w64-ucrt-x86_64-libheif
```

The app checks `C:\msys64\ucrt64\bin` after bundled/app-local encoder paths and before PATH. Package/output tests should prefer `encoders\x64`, not MSYS2.

## Version Control Notes

Keep these out of git:

- `external/`
- `bin/`
- `obj/`
- `AppPackages/`
- `.claude/`
- generated test exports
- diagnostic logs

`external/encoders/<arch>` and `external/_deps` are intentionally local/synced, not committed. Document third-party build option changes instead of committing generated dependency outputs.

## Distribution Notes

- `PublishTrimmed=false` is intentional. Trimming can break reflection-based settings and WinRT interop in ways that surface as lost settings rather than obvious crashes.
- Re-check `THIRD_PARTY_NOTICES.md` before publishing a portable zip, MSIX, or Store package that actually carries third-party native codec tools.
- Keep open-source license obligations separate from HEVC patent/commercial risk. IPC calls to user-installed tools do not create a binary redistribution event for this package; bundling those tools does.
