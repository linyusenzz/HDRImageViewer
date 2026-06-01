# 第三方组件说明

本文档用于说明 HDR Image Viewer 使用或可选调用的第三方组件。项目自身源码采用 GPLv3 或后续版本授权，见 `LICENSE`。第三方组件仍按各自许可证授权；如果你重新分发包含这些组件的二进制包，请自行确认并遵守对应许可证、源代码提供义务和平台分发要求。

本文档不是法律意见。

## NuGet / .NET 依赖

| 组件 | 用途 | 备注 |
| --- | --- | --- |
| Microsoft.WindowsAppSDK | WinUI 3 / Windows App SDK 桌面应用框架 | 由 Microsoft 发布，按其 NuGet 包许可证使用。 |
| Microsoft.Windows.SDK.BuildTools | Windows SDK / WinUI 构建支持 | 由 Microsoft 发布，按其 NuGet 包许可证使用。 |
| Microsoft.Windows.SDK.BuildTools.WinApp | `dotnet run` / packaged WinUI app 辅助构建支持 | 由 Microsoft 发布，按其 NuGet 包许可证使用。 |
| Vortice.Direct3D11 / Vortice.DXGI / Vortice.Direct2D1 / Vortice.D3DCompiler | Direct3D / DXGI / Direct2D / shader 编译相关 .NET 绑定 | 按 Vortice 项目的许可证使用。 |
| LibHeifSharp | HEIF / AVIF 容器和 HDR 解码绑定 | 项目文档中按 LGPL-3.0 处理；实际分发时请核对 NuGet 包和其 native 依赖许可证。 |
| xUnit / Microsoft.NET.Test.Sdk | 单元测试 | 仅用于测试项目，不随应用运行时作为核心功能使用。 |

## Native bridge / 构建依赖

| 组件 | 用途 | 备注 |
| --- | --- | --- |
| OpenEXR | `HdrImageViewer.Native` 的 EXR 解码 backend | GitHub workflow 通过 vcpkg 构建 OpenEXR，并把运行时 DLL 打进 portable zip。 |
| Imath / Iex / IlmThread / libdeflate / openjph 等 OpenEXR 运行时依赖 | OpenEXR transitive runtime DLL | 随 OpenEXR 构建/运行时一起进入 portable zip；请遵守各自许可证。 |
| vcpkg | CI / 本地 native 依赖安装 | 用于获取和构建 OpenEXR 等 native 依赖。 |

## 可选外部命令行工具

这些工具不是仓库源码的一部分，也不会默认提交到 git。GitHub portable zip 默认不内置 `external/encoders` 中的编码器。用户可以自行安装，应用会从 `C:\msys64\ucrt64\bin` 和 `PATH` 等位置自动发现。

| 工具 | 用途 | 常见来源 | 许可证注意 |
| --- | --- | --- | --- |
| `jxlinfo.exe` / `djxl.exe` | JPEG XL 探测和预览 | MSYS2 `mingw-w64-ucrt-x86_64-libjxl` | 按 libjxl 及其依赖许可证使用。 |
| `cjxl.exe` | JPEG XL HDR 导出 | MSYS2 `mingw-w64-ucrt-x86_64-libjxl` | 按 libjxl 及其依赖许可证使用。 |
| `avifenc.exe` | AVIF HDR 导出 | MSYS2 `mingw-w64-ucrt-x86_64-libavif` | 按 libavif、AOM/dav1d/rav1e 等实际 backend 许可证使用。 |
| `heif-enc.exe` | HEIF / HEIC HDR 导出 | MSYS2 `mingw-w64-ucrt-x86_64-libheif` | libheif 常见为 LGPL；HEIC/HEVC 编码 backend 可能涉及 x265/GPL 和专利/商用分发要求。 |
| `heif-dec.exe` / `avifdec.exe` | HEIF / AVIF 解码 fallback | MSYS2 libheif / libavif 工具包 | 当前主要用于 fallback；请遵守对应项目许可证。 |
| `ultrahdr_app.exe` | JPEG Ultra HDR / gain-map 导出 | Google libultrahdr 本地构建 | 需要遵守 libultrahdr 及其依赖许可证。 |
| `oiiotool.exe` / `magick.exe` | EXR fallback 转换路径 | OpenImageIO / ImageMagick | 仅作为 fallback 探测/转换工具；按各自许可证使用。 |

## HEIC / x265 特别说明

HEIC HDR 导出通常需要 HEVC 编码器。常见的 `heif-enc.exe` 组合会使用 `x265`，而 x265 通常是 GPL-2.0-or-later，除非使用商业授权版本。把 `heif-enc.exe`、`libx265.dll` 或相关 HEVC 编码组件一起重新分发，可能产生 GPL 源代码提供义务、商店分发限制和专利/商用授权风险。

因此，本仓库的 GitHub portable zip 默认不内置这些编码器。高级用户可以按自己的用途安装 MSYS2 工具包或其他兼容命令行工具。

## 分发策略

- 源码仓库：只提交项目源码、文档、测试和构建脚本。
- GitHub portable zip：包含应用、README、LICENSE、THIRD_PARTY_NOTICES，以及构建应用所需的低风险 runtime 文件；不默认包含可选 HDR 编码器工具包。
- Microsoft Store 包：如果未来内置额外 native 工具，发布前应重新检查许可证、源码提供义务和 Store 政策。

