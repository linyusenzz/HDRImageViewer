# HdrImageViewer.Native

Native bridge for format decoders that are awkward to call directly from C#.

The first target is OpenEXR. The C# app calls a small C ABI exported by
`HdrImageViewer.Native.dll` and receives RGBA half-float pixels that can be
wrapped as `DecodedBitmapPixelFormat.Rgba16Float` with `LinearScRgb` transfer.

Current local x64 Release output is expected at
`native/HdrImageViewer.Native/build/x64/Release`. The managed app copies
`HdrImageViewer.Native.dll` and adjacent OpenEXR runtime DLLs from that folder
when `HdrImageViewer.Native.dll` exists.

## Build

Install OpenEXR with a CMake package, then build per architecture:

```powershell
cmake -S native/HdrImageViewer.Native -B native/HdrImageViewer.Native/build/x64 -A x64
cmake --build native/HdrImageViewer.Native/build/x64 --config Release
```

From the repo root, `eng/build-native.ps1 -Platforms x64 -Configuration Release`
uses the same output layout. `eng/verify-codecs.ps1` also checks the expected
x64 native bridge runtime files alongside bundled codecs.

If OpenEXR is not found, the project builds a stub DLL that reports EXR decode
as unavailable. This is intentional so the managed app can keep compiling while
the native dependency is being prepared.

## Exported ABI

```cpp
struct HdrivExrImage
{
    int32_t width;
    int32_t height;
    uint16_t* rgbaHalfPixels;
};

int32_t hdriv_exr_decode_rgba16f(const wchar_t* path, HdrivExrImage* image, wchar_t* errorBuffer, int32_t errorBufferLength);
void hdriv_exr_free(void* pointer);
```
