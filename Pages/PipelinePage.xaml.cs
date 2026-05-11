using HdrImageViewer.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HdrImageViewer.Pages;

public sealed partial class PipelinePage : Page
{
    public PipelineViewModel ViewModel { get; } = new();

    public PipelinePage()
    {
        InitializeComponent();
    }
}
