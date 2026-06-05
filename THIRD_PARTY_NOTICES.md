# 第三方组件说明

本文档用于说明 HDR Image Viewer 使用或可选调用的第三方组件。项目自身源码采用 GPLv3 或后续版本授权，见 `LICENSE`。第三方组件仍按各自许可证授权。

需要区分两种情况：用户自行安装的外部命令行工具由本应用通过子进程调用时，本项目没有重新分发这些第三方二进制；如果发布包主动携带这些 exe/dll，才需要按被携带组件的许可证处理二进制再分发、通知和源代码提供等义务。专利、商用授权和平台政策风险是另一类问题，应与开源许可证合规分开评估。

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

这些工具不是仓库源码的一部分，也不会默认提交到 git。应用通过进程间通信/子进程调用这些 CLI，不把它们静态链接进应用。没有随包携带工具时，用户可以自行安装，应用会从 `C:\msys64\ucrt64\bin` 和 `PATH` 等位置自动发现；这种使用方式不构成本项目对这些 CLI 的二进制再分发。

当前本地 x64 开发/测试环境可把要随包携带的工具集中放在 `external/encoders/x64`，构建后复制到应用目录 `encoders/x64`；`external/_deps` 只作为跟项目文件夹同步的源码/构建缓存，不是运行时 package input。如果发布包实际包含了 `encoders/<arch>` 中的第三方 exe/dll，则需要对这些被携带文件单独处理许可证通知、源码提供义务和其他分发要求。

| 工具 | 用途 | 常见来源 | 许可证注意 |
| --- | --- | --- | --- |
| `jxlinfo.exe` / `djxl.exe` | JPEG XL 探测和预览 | MSYS2 `mingw-w64-ucrt-x86_64-libjxl` | 按 libjxl 及其依赖许可证使用。 |
| `cjxl.exe` | JPEG XL HDR 导出 | MSYS2 `mingw-w64-ucrt-x86_64-libjxl` | 按 libjxl 及其依赖许可证使用。 |
| `avifenc.exe` | AVIF HDR 导出 | MSYS2 `mingw-w64-ucrt-x86_64-libavif` | 按 libavif、AOM/dav1d/rav1e 等实际 backend 许可证使用。 |
| `heif-enc.exe` | HEIF / HEIC HDR 导出 | MSYS2 `mingw-w64-ucrt-x86_64-libheif` | 用户自行安装并由本应用子进程调用时，本项目不重新分发该工具；若随包携带，需按 libheif、实际 HEVC backend 等组件许可证处理。 |
| `heif-dec.exe` / `avifdec.exe` | HEIF / AVIF 解码 fallback | MSYS2 libheif / libavif 工具包 | 当前主要用于 fallback；请遵守对应项目许可证。 |
| `ultrahdr_app.exe` | JPEG Ultra HDR / gain-map 导出 | Google libultrahdr 本地构建 | 需要遵守 libultrahdr 及其依赖许可证。 |
| `oiiotool.exe` / `magick.exe` | EXR fallback 转换路径 | OpenImageIO / ImageMagick | 仅作为 fallback 探测/转换工具；按各自许可证使用。 |

## HEIC / x265 特别说明

HEIC HDR 导出通常需要 HEVC 编码器。常见的 `heif-enc.exe` 组合会使用 `x265`，而 x265 通常是 GPL-2.0-or-later，除非使用商业授权版本。

这里的关键边界是分发行为，而不是 IPC 调用本身：如果用户按自己的用途安装 MSYS2 工具包或其他兼容命令行工具，本应用只是启动外部进程并读取结果，不会因此把这些工具变成本项目分发的二进制。如果 GitHub portable zip、MSIX 或 Store 包主动包含 `heif-enc.exe`、`libx265.dll` 或相关 HEVC 编码组件，则需要单独确认对应许可证和源码提供义务。

专利/商业风险也要单独看。即使开源许可证义务已经满足，HEVC 编码/分发仍可能受专利许可、商用授权、地区差异和平台政策影响。

## 分发策略

- 源码仓库：只提交项目源码、文档、测试和构建脚本。
- GitHub portable zip：包含应用、README、LICENSE、THIRD_PARTY_NOTICES，以及构建输出中存在且允许分发的 runtime 文件；不随包携带的用户自装 CLI 不需要在这里按二进制再分发处理。如果纳入 `external/encoders/<arch>` 中的可选 HDR 编码器，发布前必须重新检查被纳入文件的许可证义务。
- Microsoft Store / MSIX 包：当前本地 x64 测试包可以从 `external/encoders/x64` 携带工具；对外发布前应区分“用户自装外部工具”和“包内携带第三方二进制”，只对实际随包分发的文件处理对应许可证义务，并单独评估专利/商用和 Store 政策。
