using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace HdrImageViewer.Presentation;

/// <summary>
/// Native compositor transitions for persistent surfaces that switch Visibility
/// instead of being recreated by Frame navigation. XAML owns layout and hit testing.
/// </summary>
internal sealed class VisibilityMotion(DispatcherQueue dispatcher) : IDisposable
{
    private readonly UISettings _settings = new();
    private readonly List<(UIElement Element, Vector3 Offset)> _surfaces = [];
    private bool _listening;
    private bool _disposed;

    public void Add(UIElement element, Vector3 offset)
    {
        if (!_listening)
        {
            _settings.AnimationsEnabledChanged += AnimationsEnabledChanged;
            _listening = true;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        _surfaces.Add((element, offset));
        Configure(element, offset);
    }

    private void AnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            foreach (var (element, offset) in _surfaces) Configure(element, offset);
        });

    private void Configure(UIElement element, Vector3 offset)
    {
        // Clear in-flight animations as well as future transitions when reduced
        // motion changes. No timers or completion callbacks can hide a reopened view.
        ElementCompositionPreview.SetImplicitShowAnimation(element, null);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);

        if (!_settings.AnimationsEnabled)
        {
            return;
        }

        // Entrance only: implicit hide on a child keeps its entire departing
        // page in the composition tree, overlaying the incoming page's text.
        ElementCompositionPreview.SetImplicitShowAnimation(element, CreateEntrance(visual.Compositor, offset));
    }

    private static CompositionAnimationGroup CreateEntrance(Compositor compositor, Vector3 offset)
    {
        var duration = TimeSpan.FromMilliseconds(200);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.Duration = duration;
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, easing);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Target = "Translation";
        translation.Duration = duration;
        translation.InsertKeyFrame(0, offset);
        translation.InsertKeyFrame(1, Vector3.Zero, easing);

        var group = compositor.CreateAnimationGroup();
        group.Add(opacity);
        group.Add(translation);
        return group;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_listening) _settings.AnimationsEnabledChanged -= AnimationsEnabledChanged;
        foreach (var (element, _) in _surfaces)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(element, null);
        }
        _surfaces.Clear();
    }
}
