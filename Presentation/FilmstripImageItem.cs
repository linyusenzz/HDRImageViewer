using HdrImageViewer.Infrastructure;
using Microsoft.UI.Xaml.Media;

namespace HdrImageViewer.Presentation;

public sealed class FilmstripImageItem(string path) : ObservableObject
{
    private bool _isCurrent;
    private ImageSource? _thumbnail;

    public string Path { get; } = path;

    public string FileName { get; } = System.IO.Path.GetFileName(path);

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
