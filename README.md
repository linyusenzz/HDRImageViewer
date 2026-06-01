# HDR Image Viewer

HDR Image Viewer 是一个面向 Windows 的 WinUI 3 图片查看器，重点支持 HDR 静态图片、gain map 图片和常见 HDR 容器的探测/预览。项目仍在快速开发中，当前目标是把 Ultra HDR、HEIF/AVIF、JPEG XL、OpenEXR 等格式的打开、显示、裁剪和导出流程逐步做完整。

## 当前能力

- WinUI 3 图片查看界面：文件打开、拖放、文件夹导航、缩略图胶片栏、查看器缩放/平移、沉浸式预览、裁剪 UI。
- Direct3D 11 HDR 渲染：通过 `SwapChainPanel` 呈现 FP16 scRGB swap chain。
- JPEG Ultra HDR / Adobe gain map：内置 JPEG/XMP 探测和 shader 重建路径。
- HEIF / HEIC / AVIF：探测 PQ、HLG、BT.2020、bit depth、gain-map 辅助图信号，并优先通过 LibHeifSharp 做 in-process HDR 解码。
- JPEG XL：通过可选 `jxlinfo.exe` / `djxl.exe` 做探测和预览。
- OpenEXR：通过 `HdrImageViewer.Native` + OpenEXR 解码为 RGBA16F。
- HDR 导出实验：SDR 预览导出、JPEG Ultra HDR 导出、单层 HDR JXL/AVIF/HEIF 导出。

## 格式支持状态

| 格式 | 打开 / 预览 | HDR 显示 | 导出 | 说明 |
| --- | --- | --- | --- | --- |
| JPEG / JPG | 已支持 | SDR 已支持；Ultra HDR / gain map 已支持 | SDR 已支持；Ultra HDR 需要 `ultrahdr_app.exe` | Ultra HDR、Adobe gain map、Apple HDRGainMap 信号会被探测。 |
| PNG | 已支持 | SDR / 高位深 / 部分 HDR 元数据探测 | SDR 导出已支持 | 通过 WIC 解码；支持 ICC、部分 PQ/HLG 元数据路径。 |
| TIFF / TIF | 已支持 | SDR / 高位深 / 浮点 TIFF 路径 | SDR 导出已支持 | 通过 WIC 解码，浮点/高位深图像会进入 HDR 候选路径。 |
| JPEG XR / WDP / HDP | 已支持 | 已支持 scRGB / FP16 候选路径 | 暂未作为主要导出目标 | 依赖 Windows WIC 解码能力。 |
| HEIF / HEIC | 部分支持 | 单层 PQ/HLG HDR 已支持；gain map 重建仍在推进 | 单层 HDR 导出需要 `heif-enc.exe` | HEIC HDR 优先走 LibHeifSharp；Windows 解码器和 `heif-dec.exe` 是 fallback。 |
| AVIF | 部分支持 | 单层 PQ/HLG HDR 已支持；gain map 支持仍在推进 | 单层 HDR 导出需要 `avifenc.exe` | AVIF HDR 优先走 LibHeifSharp；Windows 解码器和 `avifdec.exe` 是 fallback。 |
| JPEG XL / JXL | 需要可选工具 | 已接入预览路径 | 单层 HDR 导出需要 `cjxl.exe` | 打开/探测需要 `jxlinfo.exe` 和 `djxl.exe`。GitHub portable zip 默认不内置这些工具。 |
| OpenEXR / EXR | 已支持 | 已支持 scene-linear float/half 到 RGBA16F | 暂未支持导出 | GitHub workflow 会构建 native bridge 并把 OpenEXR 运行库打进 portable zip。 |
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

大多数基础格式可以直接打开。以下能力依赖外部命令行工具，应用会自动检查 `C:\msys64\ucrt64\bin` 和 `PATH`：

- JPEG XL 预览：`jxlinfo.exe`、`djxl.exe`。
- JPEG XL HDR 导出：`cjxl.exe`。
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

注意：`heif-enc.exe` 常见发行版会依赖 x265/HEVC，相关组件可能涉及 GPL 或专利/商用分发问题。GitHub portable zip 默认不内置这些编码器，用户可按自己的用途自行安装。

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
.\eng\publish-portable.ps1 -Version 1.0.10 -Platform x64
```

`.github/workflows/release-portable.yml` 会在推送 `v*` tag 时运行同一套脚本，并把 `artifacts/HdrImageViewer-<version>-win-x64-portable.zip` 上传到 GitHub Release。可以设置仓库变量 `STORE_URL`，让 Release notes 自动包含 Microsoft Store 链接。

## 本地开发依赖

- `external/libultrahdr` 是本地 checkout/build 依赖，已被 git 忽略。
- JPEG Ultra HDR 导出会查找 `external/libultrahdr/build/Release/ultrahdr_app.exe` 或 PATH 中的 `ultrahdr_app.exe`。
- 建议用 `UHDR_WRITE_XMP=ON` 和 `UHDR_WRITE_ISO=ON` 重新构建 libultrahdr，让导出的 JPEG 同时包含 Adobe-compatible XMP 和 ISO 21496-1 元数据。
- 可选单层 HDR 导出工具会从 PATH、MSYS2、`external/libjxl`、`external/libavif`、`external/libheif` 以及 `external/encoders/<arch>` 自动发现。
- 更多说明见 `docs/NATIVE_HDR_EXPORT.md`、`docs/HDR_PIPELINE.md` 和 `docs/DEVELOPMENT_OVERVIEW.md`。

## 路线图

- 完整验证 Ultra HDR JPEG、HLG HEIC、PQ/HLG AVIF/JXL、OpenEXR、PNG/TIFF HDR 和 SDR 文件矩阵。
- 完善 HEIF/AVIF gain-map reconstruction。
- 加入 ISO 21496-1 二进制元数据解析，并在同时存在 ISO 与 Adobe XMP 时优先使用 ISO 路径。
- 继续推进 GPU APL reduction 和显示器 ABL soft proof。
- 清理并稳定 native backend 的打包策略，避免把有额外许可证风险的编码器默认塞进 GitHub portable zip。

## 许可证

本项目采用 GPLv3 或后续版本授权，见 `LICENSE`。

第三方库、NuGet 包和用户自行安装的外部命令行工具使用各自许可证；如果你重新分发包含这些工具的二进制包，请单独确认对应许可证义务。
