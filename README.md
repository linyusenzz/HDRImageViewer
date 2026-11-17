# HDR Image Viewer

HDR Image Viewer 是一个面向 Windows 的 WinUI 3 图片查看器，重点支持 HDR 静态图片、gain map 图片和常见 HDR 容器的探测/预览。项目仍在快速开发中，当前目标是把 Ultra HDR、HEIF/AVIF、JPEG XL、OpenEXR 等格式的打开、显示、裁剪和导出流程逐步做完整。

## 当前能力

- WinUI 3 图片查看界面：文件打开、拖放、文件夹导航、缩略图胶片栏、查看器缩放/平移、沉浸式预览、裁剪 UI。
- Direct3D 11 HDR 渲染：通过 `SwapChainPanel` 呈现 FP16 scRGB swap chain。
- JPEG Ultra HDR / Adobe / ISO 21496 / Apple gain map：内置 JPEG APP2/XMP 探测和 shader 重建路径。
- HEIF / HEIC / AVIF：探测 PQ、HLG、BT.2020、Display P3、bit depth、辅助 gain-map 和 ISO tmap/gain-map 信号，并接入进程内 HDR gain-map 重建。
- JPEG XL：通过内置 `libjxl` 原生进程内探测与解码（支持单层 SDR/PQ/HLG/线性浮点、Alpha 通道及 `jhgm` gain-map 重建，`jxlinfo.exe` / `djxl.exe` 作为回退）。
- OpenEXR 与 Radiance HDR：OpenEXR 通过 `HdrImageViewer.Native` 零拷贝解码为 RGBA16F；Radiance `.hdr`（RGBE/XYZE）通过内置纯 C# 解码器直出线性 scRGB。
- Live Photo / Motion Photo：探测同名 sidecar 视频、JPEG 内嵌 Motion Photo 和 AVIF/HEIC 内的 `mpvd` 动态视频，支持基于文件流切片的零拷贝内嵌视频播放，避免 `%TEMP%` 临时文件堆积。
- 云端文件保护：OneDrive、iCloud 等在线占位文件不会在预加载、胶片栏缩略图和 sidecar 视频探测中触发自动下载，只读取不召回的文件属性并显示 shell 云预览缩略图；打开或播放仍按需下载，可在设置中关闭。
- HDR 导出：SDR 预览导出、JPEG Ultra HDR / HEIC / AVIF ISO Gain Map 导出、单层 HDR PNG（16-bit cICP）/ TIFF（32-bit float）/ JXR / EXR / JXL / AVIF / HEIC 导出与批量导出。

## 格式支持状态

| 格式 | 打开 / 预览 | HDR 显示 | 导出 | 说明 |
| --- | --- | --- | --- | --- |
| JPEG / JPG | 已支持 | SDR（含广色域 P3/Adobe RGB 保真）；Ultra HDR / Adobe / ISO 21496 / Apple gain map 已支持 | SDR 已支持；Ultra HDR 使用 `ultrahdr_app.exe` | JPEG APP2/XMP 探测会解析 Adobe XMP、ISO 21496-1 APP2、Apple HDRGainMap 和 ICC base gamut。 |
| PNG | 已支持 | SDR（含广色域）/ 高位深 / cICP PQ/HLG 元数据探测 | SDR 与 16-bit HDR PNG 已支持 | 支持 ICC（P3/BT.2020/Adobe RGB/ProPhoto）、cICP PQ/HLG 与透明通道合成。 |
| TIFF / TIF | 已支持 | SDR / 高位深 / 浮点 TIFF 路径 | SDR 与 32-bit 浮点 HDR TIFF 已支持 | 通过 WIC 解码，浮点/高位深/广色域图像会进入扩展色域或 HDR 路径。 |
| JPEG XR / WDP / HDP | 已支持 | 优先 WIC FP16/scRGB；失败时回退到 WinRT RGBA16/RGBA8 预览 | 单层 FP16 scRGB JXR 导出已支持 | 依赖 Windows WIC / Windows Imaging 解码与编码能力。 |
| HEIF / HEIC | 已支持 | 单层 PQ/HLG、广色域 SDR、Apple 辅助 gain map 和 ISO tmap 已支持 | 单层 HDR 使用 `heif-enc.exe`；ISO Gain Map 使用 `ultrahdr_app.exe` | 单层 HDR 与 aux/tmap 优先走进程内 LibHeifSharp；primary/base 走 Windows Imaging。 |
| AVIF | 已支持 | 单层 PQ/HLG、广色域 SDR 和 ISO tmap gain map 已支持 | 单层 HDR 使用 `avifenc.exe`；ISO Gain Map 使用 `ultrahdr_app.exe` | 单层 HDR 与 ISO `tmap` gain map 均通过进程内 LibHeifSharp + ISO 元数据读取器直接解码。 |
| JPEG XL / JXL | 已支持 | 单层 SDR/PQ/HLG/线性及 `jhgm` gain map 已支持 | 单层 HDR 导出需要 `cjxl.exe` | 优先通过 `libjxl.dll` 进程内完成探测、主图解码和 `jhgm` 码流解码；缺失时回退至 `jxlinfo.exe` / `djxl.exe`。 |
| OpenEXR / EXR | 已支持 | 已支持 scene-linear float/half 零拷贝解码到 RGBA16F | 单层 HDR 导出通过 native bridge | 使用 `HdrImageViewer.Native` + OpenEXR。 |
| Radiance HDR / RGBE | 已支持 | 已支持 RGBE / XYZE 游程解码到线性 RGBA16F | 可导出为其他单层 HDR / Gain Map 格式 | 内置纯 C# RGBE/RLE 解码器，无需外部工具。 |
| Live Photo / Motion Photo | 已支持 | 静态帧走现有 HDR renderer；动态片段走零拷贝流式媒体层叠加播放 | 暂未支持保留/重新导出动态照片包 | 支持同名 `.mov` / `.mp4` / `.m4v` sidecar、JPEG XMP Motion Photo 和 HEIC/AVIF `mpvd`。 |
| WebP | SDR 基线 / 取决于系统解码器 | 暂未作为 HDR 主路径 | SDR 导出 | 普通图片兼容路径，支持透明通道与 EXIF 读取。 |

## 安装

推荐普通用户从 Microsoft Store 安装。Store 版本由 Microsoft 签名，Windows 会处理安装、证书和更新。

GitHub Releases 会提供 portable zip，适合高级用户、测试用户或需要独立目录运行的用户：

1. 下载 `HdrImageViewer-<version>-win-x64-portable.zip`。
2. 解压到普通目录，例如 `C:\Apps\HdrImageViewer`。
3. 运行 `HdrImageViewer.exe`。

portable zip 需要 x64 版 .NET 10 Desktop Runtime 和 Windows App Runtime 2.5，不需要安装证书。因为 GitHub build 未签名，Windows 可能显示 SmartScreen 提示。

## 可选格式工具

大多数格式（包含 JPEG Ultra HDR、PNG、TIFF、HEIC/AVIF、OpenEXR、Radiance `.hdr` 以及内置 `libjxl.dll` 下的 JPEG XL 预览）均通过进程内解码器直接打开。以下导出或回退能力调用命令行编码工具；用户自行安装工具时，本项目没有重新分发这些第三方二进制，只有发布包主动携带 `encoders\<arch>` 中的工具时才涉及对应二进制再分发义务。运行时查找顺序是应用输出目录 `encoders\<arch>`、`C:\msys64\ucrt64\bin`，最后是 `PATH`：

- JPEG XL CLI 回退与导出：`cjxl.exe`（导出）、`djxl.exe` / `jxlinfo.exe`（当 `libjxl.dll` 不可用时的回退）。
- AVIF HDR 导出：`avifenc.exe`。
- HEIF / HEIC HDR 导出：`heif-enc.exe`。
- JPEG / HEIC / AVIF Gain Map 导出：`ultrahdr_app.exe`。

用 MSYS2 UCRT64 安装常用可选工具：

```powershell
winget install --id MSYS2.MSYS2 --source winget
C:\msys64\usr\bin\pacman.exe -Syu --noconfirm
C:\msys64\usr\bin\pacman.exe -S --needed --noconfirm mingw-w64-ucrt-x86_64-libjxl mingw-w64-ucrt-x86_64-libavif mingw-w64-ucrt-x86_64-libheif
```

安装后重启 HDR Image Viewer。

## 多语言支持 / Languages

HDR Image Viewer 现已支持完整的国际化 (i18n) 与本地化架构，内置支持 12 种语言：

| 语言代码 | 语言 (Language) | 本地化名称 (Native Name) |
| --- | --- | --- |
| `zh-CN` | Simplified Chinese (默认) | 中文（简体） |
| `en-US` | English | English |
| `ru-RU` | Russian | Русский |
| `de-DE` | German | Deutsch |
| `fr-FR` | French | Français |
| `es-ES` | Spanish | Español |
| `it-IT` | Italian | Italiano |
| `pt-BR` | Portuguese (Brazil) | Português (Brasil) |
| `ja-JP` | Japanese | 日本語 |
| `ko-KR` | Korean | 한국어 |
| `pl-PL` | Polish | Polski |
| `uk-UA` | Ukrainian | Українська |

### 资源组织架构 (Resource Architecture)

- **WinUI 3 PRI 资源**：所有本地化字符串存放于 `Strings/<locale>/Resources.resw` 标准 XML 资源文件中，XAML 控件通过 `x:Uid` 属性进行原生声明式绑定。
- **动态字符串服务 (`Localization`)**：`Services/Localization.cs` 封装了 `Microsoft.Windows.ApplicationModel.Resources.ResourceLoader`，支持运行时通过 `Localization.GetString(key, args...)` 安全格式化加载动态文本；在非 WinRT 环境（例如轻量级单元测试宿主）下，无缝降级到编译好的 `Services/FallbackResources.cs`。
- **语言匹配与持久化**：
  - 默认情况下（“跟随系统”），应用遵循 Windows 语言首选项列表并安全回退到英语与简体中文。
  - 用户可在“设置 -> 界面 -> 语言”中明确指定应用语言。语言偏好通过 `AppSettingsService` 持久化，修改后提示重启应用以确保所有 XAML 与系统控件完全重载生效。

### 如何贡献翻译或添加新语言 (Contributing Translations)

1. **添加新语言**：
   - 在 `Strings/` 目录下创建新语言文件夹（例如 `Strings/tr-TR/`），复制 `Strings/en-US/Resources.resw` 并命名为 `Resources.resw`。
   - 在 `Package.appxmanifest` 的 `<Resources>` 节点中添加对应的 `<Resource Language="..." />`。
   - 在 `Pages/SettingsPage.xaml` 的语言选择器中添加对应的 `ComboBoxItem`。
2. **更新或翻译已有资源**：
   - 使用 Visual Studio 资源编辑器或文本编辑器编辑 `Strings/<locale>/Resources.resw`。
   - 保留所有格式占位符（例如 `{0}`, `{1}`），并保持行业标准摄影与色彩学术语（如 HDR, SDR, Gain Map, Ultra HDR, OpenEXR, JPEG XL, PQ, HLG, scRGB, BT.2020, ICC 等）。
3. **一致性测试**：
   - 运行 `dotnet test`，内置的 `LocalizationTests` 会自动验证所有语言间的键名对齐、非空性以及参数占位符一致性。

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
.\eng\publish-portable.ps1 -Version 1.0.46.0 -Platform x64
```

`.github/workflows/release-portable.yml` 会在推送 `v*` tag 时运行同一套脚本，并把 `artifacts/HdrImageViewer-<version>-win-x64-portable.zip` 上传到 GitHub Release。可以设置仓库变量 `STORE_URL`，让 Release notes 自动包含 Microsoft Store 链接。

## 路线图

- 继续推进 GPU APL reduction 和显示器 ABL soft proof。
- 继续完善动态照片保真度：后续评估将 HDR 视频帧接入现有 renderer，让 Live Photo / Motion Photo 的动态片段更贴近静态 HDR 显示。

## 许可证

本项目采用 GPLv3 或后续版本授权，见 `LICENSE`。

第三方库、NuGet 包和用户自行安装的外部命令行工具使用各自许可证，见 `THIRD_PARTY_NOTICES.md`。用户自行安装并由本应用通过子进程调用的 CLI 工具，不构成本项目对这些工具的二进制再分发；如果你重新分发包含这些工具的包，请单独确认对应许可证义务。
