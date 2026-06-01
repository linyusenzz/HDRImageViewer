using HdrImageViewer.Models;
using Microsoft.UI.Xaml.Controls;

namespace HdrImageViewer.Rendering;

public interface IHdrRenderPipeline
{
    HdrRenderIntent Intent { get; set; }

    GainmapViewMode ViewMode { get; set; }

    HdrHeadroomMode HeadroomMode { get; set; }

    void Attach(SwapChainPanel panel);

    Task LoadAsync(HdrImageDocument document, CancellationToken cancellationToken);

    Task ResizeAsync(int pixelWidth, int pixelHeight, CancellationToken cancellationToken);
}
