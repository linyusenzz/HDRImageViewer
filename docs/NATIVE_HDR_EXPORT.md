# Native HDR Export Tools

This project no longer uses FFmpeg for single-layer HDR export. The viewer discovers native command-line encoders and enables matching save formats when the tools are present.
The same MSYS2 libjxl install is also used for JPEG XL preview through `jxlinfo.exe` and `djxl.exe`.

## Bundled Encoder Status - 2026-05-29

The Store/MSIX build now bundles x64 encoder tools under:

```text
external\encoders\x64
```

`HdrImageViewer.csproj` copies `external\encoders\$(Platform)\*.*` into the app output and publish directories under `encoders\$(Platform)`. `SingleLayerHdrExportService` checks the bundled `encoders\<arch>` paths before PATH/MSYS2 so the packaged app uses the shipped tools instead of an older machine-wide install.

Bundled command-line checks:

- `cjxl.exe --version` succeeds, `cjxl v0.11.2`.
- `avifenc.exe --version` succeeds, `libavif 1.4.1`, `aom v3.13.3`.
- `heif-enc.exe --version` succeeds, `libheif 1.22.2`.
- `heif-enc.exe --list-encoders` lists `x265` for HEIC and `aom` for AVIF.
- Encoding `test_pattern-HLG.png` to 10-bit HEIC with BT.2020/HLG CICP succeeds.

Current bundled encoder directory size is about `36.28 MB` uncompressed. The main size increase is `libx265.dll`, now about `16.28 MB`.

### HEIC bit-depth fix

The first bundled HEIC attempt could launch `heif-enc.exe`, but real HDR encoding failed with:

```text
Unsupported bit depth: Bit depth not supported by x265
```

Root cause: the default vcpkg `x265:x64-windows` dynamic build exposes only the 8-bit x265 API. HEIC HDR export needs a 10-bit capable encoder because the app feeds `heif-enc` a 16-bit PQ/HLG BT.2020 PNG and asks libheif/x265 for 10-bit HEVC output.

Fix: build a multilib x265 DLL:

1. Build 12-bit x265 static library with `HIGH_BIT_DEPTH=ON` and `MAIN12=ON`.
2. Build 10-bit x265 static library with `HIGH_BIT_DEPTH=ON`.
3. Build the 8-bit shared `libx265.dll` with `LINKED_10BIT=ON`, `LINKED_12BIT=ON`, and `EXTRA_LIB` pointing at the 10-bit and 12-bit static libraries.
4. Replace the bundled `external\encoders\x64\libx265.dll` with that multilib DLL.

The app-side HEIC export still uses:

- `--bit-depth 10`
- BT.2020 primaries: `--colour_primaries 9`
- HLG or PQ transfer: `--transfer_characteristic 18` or `16`
- BT.2020 non-constant matrix: `--matrix_coefficients 9`
- full range and CLLI metadata

### Packaging and license note

JXL and AVIF bundling are relatively straightforward. HEIC/HEVC is different:

- `libheif` is LGPL-3.0-only.
- `x265` is GPL-2.0-or-later unless a commercial x265 license is obtained.
- Bundling `heif-enc.exe` plus `libx265.dll` in an MSIX may create GPL source/distribution obligations and Store/commercial distribution risk.

Before shipping HEIC export in the Store package, decide whether to keep it bundled, make it optional/external, or use a commercially licensed HEVC encoder.

## Installed Toolchain

Installed on this machine with MSYS2 UCRT64:

```powershell
winget install --id MSYS2.MSYS2 --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity
C:\msys64\usr\bin\pacman.exe -Syu --noconfirm
C:\msys64\usr\bin\pacman.exe -S --noconfirm --needed mingw-w64-ucrt-x86_64-libjxl mingw-w64-ucrt-x86_64-libavif mingw-w64-ucrt-x86_64-libheif
```

Installed executables:

- `C:\msys64\ucrt64\bin\cjxl.exe` - JPEG XL encoder, `cjxl v0.11.2`.
- `C:\msys64\ucrt64\bin\djxl.exe` and `jxlinfo.exe` - JPEG XL decode/probe tools.
- `C:\msys64\ucrt64\bin\avifenc.exe` - AVIF encoder, `libavif 1.4.1`.
- `C:\msys64\ucrt64\bin\heif-enc.exe` - HEIF/HEIC encoder, `libheif 1.21.2`.

`C:\msys64\ucrt64\bin` has been added to the user `PATH`. New terminals and newly launched app processes should find these tools through PATH. The app also checks this absolute MSYS2 path directly, but only after bundled encoder paths.

## App Behavior

- The main viewer toolbar has a single-layer HDR save-as button next to the crop command.
- Main-toolbar single-layer HDR export exposes HLG target peak and gain-map reconstruction strength before opening the save picker.
- The crop overlay can still export only the selected crop rectangle.
- Both paths use the same single-layer HDR format list:
  - `.jxl` through `cjxl.exe`
  - `.avif` through `avifenc.exe`
  - `.heic` through `heif-enc.exe`
- `SingleLayerHdrExportService` writes a temporary 16-bit PQ or HLG BT.2020 PNG, then passes it to the selected native encoder.
- Native export keeps an 80 nit reference white. The main-toolbar export dialog can override the HLG target peak for single-layer HLG output and can reduce gain-map reconstruction strength when baking gain-map sources into single-layer HDR.

## Notes

- If a tool is missing, that format is not added to the save picker.
- Existing JPEG Ultra HDR / gain-map export still uses the local `libultrahdr` CLI.
- HEIC export depends on the HEVC encoder support built into the installed `libheif` package.
- Store packaging should be tested from the app output/MSIX, not only from the developer PATH, because DLL search order and encoder bit-depth support are the failure-prone parts.
