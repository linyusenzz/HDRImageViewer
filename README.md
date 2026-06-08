# HDR Image Viewer

HDR Image Viewer 是一个面向 Windows 的 WinUI 3 图片查看器，重点支持 HDR 静态图片、gain map 图片和常见 HDR 容器的探测/预览。项目仍在快速开发中，当前目标是把 Ultra HDR、HEIF/AVIF、JPEG XL、OpenEXR 等格式的打开、显示、裁剪和导出流程逐步做完整。

## 当前能力

- WinUI 3 图片查看界面：文件打开、拖放、文件夹导航、缩略图胶片栏、查看器缩放/平移、沉浸式预览、裁剪 UI。
- Direct3D 11 HDR 渲染：通过 `SwapChainPanel` 呈现 FP16 scRGB swap chain。
- JPEG Ultra HDR / Adobe gain map：内置 JPEG/XMP 探测和 shader 重建路径。
- HEIF / HEIC / AVIF：探测 PQ、HLG、BT.2020、bit depth、辅助 gain-map 和 ISO tmap/gain-map 信号，并接入 HDR gain-map 重建。
- JPEG XL：通过可选 `jxlinfo.exe` / `djxl.exe` 做探测、预览和 jhgm gain-map 重建。
- OpenEXR：通过 `HdrImageViewer.Native` + OpenEXR 解码为 RGBA16F。
- HDR 导出实验：SDR 预览导出、JPEG Ultra HDR 导出、单层 HDR PNG/TIFF/EXR/JXL/AVIF/HEIF 导出。

## 格式支持状态

| 格式 | 打开 / 预览 | HDR 显示 | 导出 | 说明 |
| --- | --- | --- | --- | --- |
| JPEG / JPG | 已支持 | SDR 已支持；Ultra HDR / gain map 已支持 | SDR 已支持；Ultra HDR 需要 `ultrahdr_app.exe` | Ultra HDR、Adobe gain map、Apple HDRGainMap 信号会被探测。 |
| PNG | 已支持 | SDR / 高位深 / 部分 HDR 元数据探测 | SDR 导出已支持 | 通过 WIC 解码；支持 ICC、部分 PQ/HLG 元数据路径。 |
| TIFF / TIF | 已支持 | SDR / 高位深 / 浮点 TIFF 路径 | SDR 导出已支持 | 通过 WIC 解码，浮点/高位深图像会进入 HDR 候选路径。 |
| JPEG XR / WDP / HDP | 已支持 | 已支持 scRGB / FP16 候选路径 | 暂未作为主要导出目标 | 依赖 Windows WIC 解码能力。 |
| HEIF / HEIC | 部分支持 | 单层 PQ/HLG HDR 已支持；Apple/Adobe/ISO gain map 辅助图和 ISO tmap 已接入重建路径 | 单层 HDR 导出需要 `heif-enc.exe` | 单层 HDR 优先走 LibHeifSharp；失败后依次尝试 native CLI、WIC FP16、WinRT RGBA16。gain map HEIC 的 primary/base 走 Windows Imaging，aux/tmap gain map 走 LibHeifSharp。 |
| AVIF | 部分支持 | 单层 PQ/HLG HDR 已支持；ISO gain map 已接入重建路径 | 单层 HDR 导出需要 `avifenc.exe` | AVIF HDR 优先走 LibHeifSharp；gain-map AVIF 使用 `avifgainmaputil.exe` 提取 gain 图和 ISO metadata。 |
| JPEG XL / JXL | 需要可选工具 | 单层 HDR 和 jhgm gain map 已接入预览/重建路径 | 单层 HDR 导出需要 `cjxl.exe` | 打开/探测需要 `jxlinfo.exe` 和 `djxl.exe`。当前本地 x64 bundled 工具已放在 `external\encoders\x64`。 |
| OpenEXR / EXR | 已支持 | 已支持 scene-linear float/half 到 RGBA16F | 单层 HDR 导出需要 native bridge | `HdrImageViewer.Native` + OpenEXR 当前 x64 Release build 已可用；缺失时 EXR 后端会显示不可用。 |
| Radiance HDR / RGBE | 计划中 | 计划中 | 暂未支持 | 文件类型入口已预留，解码器尚未完成。 |
| WebP | SDR 基线 / 取决于系统解码器 | 暂未作为 HDR 主路径 | 暂未支持 | 当前是普通图片兼容路径，不是重点 HDR 格式。 |

## 安装

推荐普通用户从 Microsoft Store 安装。Store 版本由 Microsoft 签名，Windows 会处理安装、证书和更新。

GitHub Releases 会提供 portable zip，适合高级用户、测试用户或需要独立目录运行的用户：

1. 下载 `HdrImageViewer-<version>-win-x64-portable.zip`。
2. 解压到普通目录，例如 `C:\Apps\HdrImageViewer`。
3. 运行 `HdrImageViewer.exe`。

portable zip 不需要安装证书。因为 GitHub build 未签名，Windows 可能显示 SmartScreen 提示。

## 可选格式工具

大多数基础格式可以直接打开。以下能力依赖外部命令行工具，应用通过子进程调用这些工具，不静态链接到这些 CLI。用户自行安装工具时，本项目没有重新分发这些第三方二进制；只有发布包主动携带 `encoders\<arch>` 中的工具时，才需要按被携带组件的许可证处理二进制再分发义务。运行时查找顺序是应用目录 `encoders\<arch>`、项目目录 `external\encoders\<arch>`、`C:\msys64\ucrt64\bin`，最后才是 `PATH`：

- JPEG XL 预览：`jxlinfo.exe`、`djxl.exe`。
- JPEG XL HDR 导出：`cjxl.exe`。
- AVIF gain-map 预览：`avifgainmaputil.exe`。
- AVIF HDR 导出：`avifenc.exe`。
- HEIF / HEIC HDR 导出：`heif-enc.exe`。
- JPEG Ultra HDR 导出：`ultrahdr_app.exe`。

用 MSYS2 UCRT64 安装常用可选工具：

```powershell
winget install --id MSYS2.MSYS2 --source winget
C:\msys64\usr\bin\pacman.exe -Syu --noconfirm
C:\msys64\usr\bin\pacman.exe -S --needed --noconfirm mingw-w64-ucrt-x86_64-libjxl mingw-w64-ucrt-x86_64-libavif mingw-w64-ucrt-x86_64-libheif
```

安装后重启 HDR Image Viewer。

注意：许可证合规和 HEVC 专利/商业风险需要分开看。通过进程间通信调用用户机器上已有的 `heif-enc.exe`，不等于本项目重新分发 x265/libheif 等第三方二进制；如果发布包主动携带这些 exe/dll，才需要检查相应许可证和源码提供义务。HEVC 专利、商用授权、商店政策等问题即使在许可证合规之外，也可能需要按发布地区和分发方式单独评估。

## 构建

```powershell
dotnet build .\HdrImageViewer.csproj -p:Platform=x64
```

运行：

```powershell
dotnet run --project .\HdrImageViewer.csproj -p:Platform=x64 --no-build
```

生成本地 portable zip：

```powershell
.\eng\publish-portable.ps1 -Version 1.0.14.0 -Platform x64
```

`.github/workflows/release-portable.yml` 会在推送 `v*` tag 时运行同一套脚本，并把 `artifacts/HdrImageViewer-<version>-win-x64-portable.zip` 上传到 GitHub Release。可以设置仓库变量 `STORE_URL`，让 Release notes 自动包含 Microsoft Store 链接。

## 本地开发依赖

- `external/encoders/<arch>` 是 bundled 编解码器的本地来源目录；当前只维护 x64，`bin/`、`obj/`、`AppPackages/` 里的副本都是构建产物。AVIF gain-map 工具独立放在 `external/encoders/x64/avifgainmaputil`，避免覆盖根目录现有 AVIF/HEIF DLL。
- `external/_deps/` 是跟项目文件夹一起同步的本机依赖缓存，放第三方源码、构建目录和可重建材料，例如 `libultrahdr`、`libheif`、`libavif`、`x265-multilib`。
- JPEG Ultra HDR 导出需要 `external/encoders/x64/ultrahdr_app.exe`。如果本地已有 `external/_deps/libultrahdr/build/Release/ultrahdr_app.exe`，可运行 `.\eng\verify-codecs.ps1 -RepairUltraHdr` 补到 bundled 来源目录。
- 建议用 `UHDR_WRITE_XMP=ON` 和 `UHDR_WRITE_ISO=ON` 重新构建 libultrahdr，让导出的 JPEG 同时包含 Adobe-compatible XMP 和 ISO 21496-1 元数据。
- 可选单层 HDR 导出工具的查找顺序是 `encoders/<arch>`、`external/encoders/<arch>`、MSYS2 UCRT64、PATH。
- 用 `.\eng\verify-codecs.ps1` 检查本机 x64 bundled 编解码器和 OpenEXR native bridge 是否齐全。
- 更多说明见 `docs/ARCHITECTURE.md`、`docs/CODECS_AND_FORMATS.md` 和 `docs/BUILD_AND_PACKAGING.md`。

## 路线图

- 继续推进 GPU APL reduction 和显示器 ABL soft proof。
- 继续完善 Live Photo / Motion Photo 支持：首版已接入动态提示、同名 sidecar/内嵌 Motion Photo 探测和 WinUI 原生叠加播放；后续评估将 HDR 视频帧接入现有 renderer 以进一步贴近静态 HDR 显示。FFmpeg 只作为可选的抽取、转封装或兼容 fallback，暂不把 MPV 作为默认播放内核。

## 许可证

本项目采用 GPLv3 或后续版本授权，见 `LICENSE`。

第三方库、NuGet 包和用户自行安装的外部命令行工具使用各自许可证，见 `THIRD_PARTY_NOTICES.md`。用户自行安装并由本应用通过子进程调用的 CLI 工具，不构成本项目对这些工具的二进制再分发；如果你重新分发包含这些工具的包，请单独确认对应许可证义务。
