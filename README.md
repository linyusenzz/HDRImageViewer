# HDR Image Viewer

HDR Image Viewer 是一个面向 Windows 的 WinUI 3 图片查看器，重点支持 HDR 静态图片、gain map 图片和常见 HDR 容器的探测/预览。项目仍在快速开发中，当前目标是把 Ultra HDR、HEIF/AVIF、JPEG XL、OpenEXR 等格式的打开、显示、裁剪和导出流程逐步做完整。

## 当前能力

- WinUI 3 图片查看界面：文件打开、拖放、文件夹导航、缩略图胶片栏、查看器缩放/平移、沉浸式预览、裁剪 UI。
- Direct3D 11 HDR 渲染：通过 `SwapChainPanel` 呈现 FP16 scRGB swap chain。
- JPEG Ultra HDR / Adobe / ISO 21496 / Apple gain map：内置 JPEG APP2/XMP 探测和 shader 重建路径。
- HEIF / HEIC / AVIF：探测 PQ、HLG、BT.2020、bit depth、辅助 gain-map 和 ISO tmap/gain-map 信号，并接入 HDR gain-map 重建。
- JPEG XL：通过可选 `jxlinfo.exe` / `djxl.exe` 做探测、预览和 jhgm gain-map 重建。
- OpenEXR：通过 `HdrImageViewer.Native` + OpenEXR 解码为 RGBA16F。
- Live Photo / Motion Photo：探测同名 sidecar 视频和 JPEG 内嵌 Motion Photo，显示动态提示并用 WinUI 原生媒体层叠加播放。
- HDR 导出实验：SDR 预览导出、JPEG Ultra HDR 导出、单层 HDR PNG/TIFF/EXR/JXL/AVIF/HEIF 导出。

## 格式支持状态

| 格式 | 打开 / 预览 | HDR 显示 | 导出 | 说明 |
| --- | --- | --- | --- | --- |
| JPEG / JPG | 已支持 | SDR 已支持；Ultra HDR / Adobe / ISO 21496 / Apple gain map 已支持 | SDR 已支持；Ultra HDR 需要 `ultrahdr_app.exe` | JPEG APP2/XMP 探测会解析 Adobe XMP、ISO 21496-1 APP2、Apple HDRGainMap 和 ICC base gamut。 |
| PNG | 已支持 | SDR / 高位深 / 部分 HDR 元数据探测 | SDR 导出已支持 | 通过 WIC 解码；支持 ICC、部分 PQ/HLG 元数据路径。 |
| TIFF / TIF | 已支持 | SDR / 高位深 / 浮点 TIFF 路径 | SDR 导出已支持 | 通过 WIC 解码，浮点/高位深图像会进入 HDR 候选路径。 |
| JPEG XR / WDP / HDP | 已支持 | 优先 WIC FP16/scRGB；失败时回退到 WinRT RGBA16/RGBA8 预览 | 暂未作为主要导出目标 | 依赖 Windows WIC / Windows Imaging 解码能力，诊断栏会显示实际路径和 fallback 原因。 |
| HEIF / HEIC | 部分支持 | 单层 PQ/HLG HDR 已支持；Apple/Adobe/ISO gain map 辅助图和 ISO tmap 已接入重建路径 | 单层 HDR 导出需要 `heif-enc.exe` | 单层 HDR 优先走 LibHeifSharp；失败后依次尝试 native CLI、WIC FP16、WinRT RGBA16。gain map HEIC 的 primary/base 走 Windows Imaging，aux/tmap gain map 走 LibHeifSharp。 |
| AVIF | 部分支持 | 单层 PQ/HLG HDR 已支持；ISO gain map 已接入重建路径 | 单层 HDR 导出需要 `avifenc.exe` | AVIF HDR 优先走 LibHeifSharp；gain-map AVIF 使用 `avifgainmaputil.exe` 提取 gain 图和 ISO metadata。 |
| JPEG XL / JXL | 需要可选工具 | 单层 HDR 和 jhgm gain map 已接入预览/重建路径 | 单层 HDR 导出需要 `cjxl.exe` | 打开/探测需要 `jxlinfo.exe` 和 `djxl.exe`。当前本地 x64 bundled 工具已放在 `external\encoders\x64`。 |
| OpenEXR / EXR | 已支持 | 已支持 scene-linear float/half 到 RGBA16F | 单层 HDR 导出需要 native bridge | `HdrImageViewer.Native` + OpenEXR 当前 x64 Release build 已可用；缺失时 EXR 后端会显示不可用。 |
| Live Photo / Motion Photo | 已支持 | 静态帧走现有 HDR renderer；动态片段用 WinUI 原生媒体层叠加播放 | 暂未支持保留/重新导出动态照片包 | 支持同名 `.mov` / `.mp4` / `.m4v` sidecar 和 JPEG XMP 内嵌 Motion Photo；诊断面板会显示 companion video 的 HEVC / BT.2020 / PQ / HLG 信号。 |
| Radiance HDR / RGBE | 计划中 | 计划中 | 暂未支持 | 文件类型入口已预留，解码器尚未完成。 |
| WebP | SDR 基线 / 取决于系统解码器 | 暂未作为 HDR 主路径 | 暂未支持 | 当前是普通图片兼容路径，不是重点 HDR 格式。 |

## 安装

推荐普通用户从 Microsoft Store 安装。Store 版本由 Microsoft 签名，Windows 会处理安装、证书和更新。

GitHub Releases 会提供 portable zip，适合高级用户、测试用户或需要独立目录运行的用户：

1. 下载 `HdrImageViewer-<version>-win-x64-portable.zip`。
2. 解压到普通目录，例如 `C:\Apps\HdrImageViewer`。
3. 运行 `HdrImageViewer.exe`。

portable zip 需要 x64 版 .NET 10 Desktop Runtime 和 Windows App Runtime 2.2，不需要安装证书。因为 GitHub build 未签名，Windows 可能显示 SmartScreen 提示。

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
.\eng\publish-portable.ps1 -Version 1.0.36.0 -Platform x64
```

`.github/workflows/release-portable.yml` 会在推送 `v*` tag 时运行同一套脚本，并把 `artifacts/HdrImageViewer-<version>-win-x64-portable.zip` 上传到 GitHub Release。可以设置仓库变量 `STORE_URL`，让 Release notes 自动包含 Microsoft Store 链接。

## 路线图

- 继续推进 GPU APL reduction 和显示器 ABL soft proof。
- 继续完善动态照片保真度：后续评估将 HDR 视频帧接入现有 renderer，让 Live Photo / Motion Photo 的动态片段更贴近静态 HDR 显示。

## 许可证

本项目采用 GPLv3 或后续版本授权，见 `LICENSE`。

第三方库、NuGet 包和用户自行安装的外部命令行工具使用各自许可证，见 `THIRD_PARTY_NOTICES.md`。用户自行安装并由本应用通过子进程调用的 CLI 工具，不构成本项目对这些工具的二进制再分发；如果你重新分发包含这些工具的包，请单独确认对应许可证义务。
