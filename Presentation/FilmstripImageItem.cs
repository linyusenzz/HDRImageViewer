using HdrImageViewer.Infrastructure;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HdrImageViewer.Presentation;

public sealed class FilmstripImageItem(string path) : ObservableObject, IFilmstripThumbnailItem<ImageSource>
{
    private bool _isCurrent;
    private ImageSource? _thumbnail;
    private bool _isLoading;
    private bool _hasLoadError;
    private readonly FilmstripPreviewGeometry _previewGeometry = new();

    public string Path { get; } = path;

    public string FileName { get; } = System.IO.Path.GetFileName(path);

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                // Use the decoded, oriented dimensions. Keep the last width
                // when the image is evicted so scrolling does not resize it.
                if (value is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
                {
                    // The 56-DIP frame has 4-DIP margins and a 1-DIP border.
                    if (_previewGeometry.SetDimensions(bitmap.PixelWidth, bitmap.PixelHeight, decodedPixels: true))
                        OnPropertyChanged(nameof(PreviewWidth));
                }
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public double PreviewWidth => _previewGeometry.Width;
    internal bool HasPreviewDimensions => _previewGeometry.HasDimensions;

    public void SetPreviewDimensions(double width, double height)
    {
        if (_previewGeometry.SetDimensions(width, height)) OnPropertyChanged(nameof(PreviewWidth));
    }

    public void InvalidatePreviewDimensions() => _previewGeometry.Invalidate();

    public bool HasThumbnail => Thumbnail is not null;

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool HasLoadError
    {
        get => _hasLoadError;
        set
        {
            if (SetProperty(ref _hasLoadError, value))
                OnPropertyChanged(nameof(PreviewDescription));
        }
    }

    public string PreviewDescription => HasLoadError ? Localization.GetString("FilmstripThumbnailLoadErrorFormat", FileName) : FileName;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
