using HdrImageViewer.Infrastructure;

namespace HdrImageViewer.ViewModels;

public sealed class PipelineViewModel : ObservableObject
{
    public IReadOnlyList<PipelineStage> Stages { get; } =
    [
        new("探测", "读取容器标记、ICC、EXIF 方向、HDR 元数据以及 gain map 信号。"),
        new("解码", "用 WIC/FFmpeg 解码 SDR/HDR 像素，并把可用的 gain map 上传为 D3D 纹理。"),
        new("归一化", "把源色彩转换到内部 scene-linear scRGB 呈现空间。"),
        new("重建", "在像素着色器中应用 gain map 元数据，或映射单层 HDR 的传递函数。"),
        new("呈现", "通过 SwapChainPanel 承载的 FP16 scRGB DirectX swap chain 输出。"),
    ];
}

public sealed record PipelineStage(string Name, string Description);
