# Pinned x64 codec stack (updated 2026-10-06)

| Component | Previous local version | Updated version |
| --- | --- | --- |
| libultrahdr | 1.4.0 | 2.0.2 + main snapshot `66821e0a261a` |
| libjxl | 0.11.2 | 0.12.0 |
| libavif / avifgainmaputil | 1.4.1 | 1.4.2 |
| libheif | 1.22.2 | 1.23.5 |
| libde265 | 1.1.0 | 1.1.3 |
| OpenEXR bridge backend | 3.4.13 | 3.5.1 |
| Imath | 3.2.2 | 3.2.3 |
| OpenJPH | 0.30.1 | 0.32.0 |
| libdeflate | 1.25 | 1.26 |

The UCRT64 bundle also includes x265 4.3 with 8/10/12-bit APIs, AOM 3.15.1,
dav1d 1.5.4, SVT-AV1 4.2.0, libjpeg-turbo 3.2.0 and Little CMS 2.19.1.
Exact package revisions, source URLs and SHA256 values are in `eng/codecs.lock.json`.
Windows App SDK, Vortice, LibHeifSharp and test-framework package versions are unchanged.

## Ultra HDR source update (2026-10-06)

The latest upstream release is still v2.0.2. The bundle now pins main commit
[`66821e0a261aa3a06c0e7c889f52eced52850be1`](https://github.com/google/libultrahdr/commit/66821e0a261aa3a06c0e7c889f52eced52850be1),
32 commits after that release. This is a source snapshot, not a new stable release;
the CLI intentionally still reports v2.0.2. The full revision, source archive and
SHA256 are recorded in the lock copied into the bundle.

The upstream changes include odd-dimension pixel-processing fixes, stricter
gain-map metadata validation and final ISO metadata writing, hardened HEIF/AVIF
writers, sRGB NCLX signaling, alpha preservation, Windows UTF-8 CLI support,
and a runtime-aware gain-map routing API. The viewer continues using its existing
export/decode paths; this update does not adopt the new routing API.
Other codec versions and the private libheif revision are unchanged.

## Restore and verify

Requires Python 3.14+ (standard-library Zstandard extraction), PowerShell 7,
and an existing MSYS2 UCRT64 GCC/CMake/Ninja/binutils installation. No packages
are installed into or updated in the developer's global MSYS2 installation.

```powershell
python eng/build-codecs.py --apply
./eng/verify-codecs.ps1
```

Use `--msys <MSYS2 root>` for a non-default toolchain location. Without
`--apply`, the script only stages the rebuilt bundle. It verifies downloads,
builds Ultra HDR and OpenEXR from pinned source, follows PE imports to collect
runtime DLLs, copies licenses, writes a runtime checksum manifest, and checks
CLI startup with MSYS2 removed from PATH. Existing runtime directories are
renamed into `external/_deps/codec-upgrade/backups/<id>` before replacement;
failed directory replacement rolls back. Close any process using those DLLs
before applying. An ASCII-path junction under TEMP avoids GNU ld's Unicode path
limitation; it points at this checkout and contains no separate source copy.

`eng/build-native.ps1` now uses this path for default Release builds. Passing
an explicit `-VcpkgRoot` retains the legacy developer bridge build. CI and
portable release workflows provision the build toolchain and use the same lock.
The workflow changes require a subsequent CI run; local validation does not
constitute a completed remote CI run.

## Runtime details

- `external/encoders/x64` contains a single consistent UCRT64 tool set, including
  `avifgainmaputil.exe`; its previous separate subdirectory is no longer needed.
- The bridge uses OpenEXR 3.5.1. Prebuilt MSYS2 libjxl links against the maintained
  OpenEXR 3.4.15 ABI; that separately named runtime remains beside the JXL tools.
- Both `libjxl.dll` and `jxl.dll` naming conventions are supported by the shared
  .NET native-library resolver. Threads are loaded from the same directory and
  naming family to avoid mixing incompatible builds.
- Runtime copying uses `Always`: upstream archive timestamps can be older than
  a locally built previous version, so `PreserveNewest` can keep an obsolete exe
  beside new DLLs. Publish to a fresh directory to exclude removed legacy files.
- Ultra HDR is built with `UHDR_WRITE_XMP=ON`, `UHDR_WRITE_ISO=ON`,
  `UHDR_ENABLE_HEIF=ON`, `UHDR_ENABLE_GLES=OFF`. HEIC and AVIF Gain Map exports
  use the upstream PR1503 backend pinned at libheif commit
  `4a3f74bc593ebfc29becc1ed5dd0a61cc66d40e1` (1.19.7 base), with the patch
  shipped in libultrahdr 2.0.2. Its DLL is named `libheif-uhdr.dll`; it does not
  replace libheif 1.23.5 used by application decoding and ordinary HEIC export.
  The private backend shares the current x265/AOM/libde265 codec libraries.
- The build puts the private HEIF headers before the general dependency prefix.
  CMake definition-list handling and the HEIF/AVIF sRGB NCLX transfer marker are
  now fixed upstream, so their previous local patches have been removed.
  Source checksums and the exact upstream patch are reproducible from the lock
  and build script.
- Every staged bundle must encode and decode JPEG, HEIC and AVIF Gain Map at
  odd dimensions (33x17) with a clean PATH before replacing runtime directories.
- Single-image, crop, and batch exports support JPEG/HEIC/AVIF Gain Map.
  Automatic HDR-to-SDR mapping produces a Display P3 base. The old gamut chooser
  incorrectly treated CLI `-c` (SDR input gamut) as an output option; the UI now
  states the actual fixed output. JPEG preserving operations remain JPEG-only.
- ISO tmap HEIC/AVIF bases and gain maps decode using current libheif in-process.
  This respects odd-size clean apertures without depending on Windows codecs.
  Apple auxiliary gain maps retain their existing Windows primary decode path.

## Validation

The integration runner supports `--codecs` after its two existing arguments:

```powershell
dotnet build tests/HdrImageViewer.IntegrationTests -c Release -p:Platform=x64 -p:PortableBuild=true
# Run the built console runner with a 640x320 HDR PNG and a new output directory:
# HdrImageViewer.IntegrationTests.exe <HDR PNG> <empty output directory> --codecs
```

It exercises application-level PQ and HLG AVIF/HEIC/JXL exports, in-process HDR
decode, EXR half-float export/decode, and monochrome/RGB JPEG Ultra HDR with both
XMP and ISO metadata. It also tests monochrome/RGB HEIC and AVIF Gain Map,
odd-sized crops, batch output, reconstruction against libultrahdr, and existing
file preservation on failure/cancellation. `HDRVIEWER_AVIF_GAINMAP_FIXTURE` can point to an existing
AVIF ISO gain-map sample to also check metadata parsing and extraction.
EXR byte-for-byte and JXL CLI/native parity checks remain in the unit suite.

The unit-test project copies the current bundle to its output's `encoders/x64`
and bridge DLLs to the output root when present. Without a restored bundle,
tests may use installed MSYS2 tools or skip missing prerequisites. EXR external-converter
parity still requires `oiiotool` or `magick` in addition to the shipped codecs.

Local validation on 2026-10-02: Release build completed with zero warnings/errors;
portable publish succeeded. 136 unit tests passed; the external EXR converter
parity test was skipped because its converter was unavailable. The codec suite
passed JPEG, ordinary HDR exports, HEIC/AVIF Gain Map monochrome/RGB, 257x129
crops, 128-pixel previews, batch exports, HEIC-to-AVIF Gain Map re-export,
cancellation/failure preservation, and the existing AVIF gain-map fixture.
For each generated Gain Map format/channel combination, 1,152 reconstructed
HDR samples were compared against libultrahdr; relative RMS error was
0.15%-0.17%. This measures decoder agreement on the synthetic test image, not
lossless agreement with every original HDR image.
All 70 runtime files matched the manifest in the fresh published output.
The viewer displayed the HEIC Gain Map sample. The batch format choices and
Ctrl+S > Gain Map HDR > JPEG/AVIF/HEIC save types were verified in the running UI.
Remote CI and third-party application compatibility have not been tested.

Local validation on 2026-10-06 for `66821e0a261a`: Release app build succeeded
with zero warnings/errors; all 234 unit tests passed with native-test requirements
enabled (the existing local ImageMagick tool supplied the EXR/PFM comparison).
The integration runner retained its four existing CS0436 WinAppSDK initializer
warnings. All 33 codec integration checks passed, including JPEG XMP/ISO output,
HEIC/AVIF RGB and monochrome gain maps, 257x129 crops, previews, batch output,
HEIC-to-AVIF re-export, failure/cancellation preservation, and an older AVIF
gain-map fixture. HEIC/AVIF reconstruction agreement with the updated CLI was
0.154%-0.166% relative RMS on the synthetic fixture. All 70 bundled runtime files
passed SHA256 verification. These checks preceded app release 1.0.36.0; see
`CHANGELOG.md` for the release scope.
