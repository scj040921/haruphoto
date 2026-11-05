using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private bool _previewIsLoading;
    private ImageSource? _previewLastReady;
    private Storyboard? _previewSoftAnimation;
    private Storyboard? _previewPaletteAnimation;
    private CompositionVisualSurface? _previewFocusSurface;
    private CompositionSurfaceBrush? _previewFocusSource;
    private CompositionEffectFactory? _previewFocusFactory;
    private CompositionEffectBrush? _previewFocusBrush;
    private SpriteVisual? _previewFocusVisual;
    private bool _previewFocusUnavailable;
    private long _previewFocusStarted;
#if PERFORMANCE_TEST
    private TaskCompletionSource<bool>? _previewTestDecodeGate;
#endif

    private async Task PreviewImageOpenedAsync(int request, BitmapImage image)
    {
        await Task.Yield();
#if PERFORMANCE_TEST
        var gate = _previewTestDecodeGate;
        if (AppPaths.IsTestProfile && gate != null) await gate.Task;
#endif
        if (request != _previewTransitionRequest || _windowClosing || !ReferenceEquals(PreviewImage.Source, image)) return;
        if (MotionEnabled)
        {
            if (PreviewSoftImage.Source == null) PresentPreviewSoft(image, request);
            var elapsed = _previewFocusStarted == 0 ? 100 : System.Diagnostics.Stopwatch.GetElapsedTime(_previewFocusStarted).TotalMilliseconds;
            if (elapsed < 100) await Task.Delay((int)Math.Ceiling(100 - elapsed));
            if (request != _previewTransitionRequest || _windowClosing || !ReferenceEquals(PreviewImage.Source, image)) return;
        }
        var currentOpacity = PreviewSoftLayer.Opacity;
        var outgoingOpacity = PreviewImageOutgoing.Opacity;
        _previewSoftAnimation?.Stop(); _previewSoftAnimation = null;
        PreviewSoftLayer.Opacity = currentOpacity;
        PreviewImageOutgoing.Opacity = outgoingOpacity;
        FinishPreviewImageTransition(request);
    }

    private async Task ShowPreviewThumbnailAsync(PhotoItem photo, int request)
    {
        try
        {
            var thumbnail = photo.ThumbnailSource;
            if (thumbnail == null && _filmstripCts != null)
            {
                await EnsureFilmstripThumbnailAsync(photo, _filmstripCts.Token);
                thumbnail = photo.ThumbnailSource;
            }
            if (thumbnail != null) PresentPreviewSoft(thumbnail, request);
        }
        catch (OperationCanceledException) { }
        catch { /* 原图解码继续；无法读取时由原图失败事件显示说明。 */ }
    }

    private void PresentPreviewSoft(ImageSource thumbnail, int request)
    {
            if (request != _previewTransitionRequest || !_previewIsLoading || _windowClosing || PreviewSoftImage.Source != null) return;
            PreviewSoftImage.Source = thumbnail; _previewFocusStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (MotionEnabled) StartPreviewFocusBlur();
            _previewSoftAnimation?.Stop();
            if (!MotionEnabled) { PreviewSoftLayer.Opacity = 1; PreviewImageOutgoing.Opacity = 0; return; }
            PreviewSoftLayer.Opacity = 1;
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(100),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fade, PreviewSoftLayer); Storyboard.SetTargetProperty(fade, "Opacity");
            var outgoingFade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(100),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(outgoingFade, PreviewImageOutgoing); Storyboard.SetTargetProperty(outgoingFade, "Opacity");
            _previewSoftAnimation = new Storyboard(); _previewSoftAnimation.Children.Add(fade); _previewSoftAnimation.Children.Add(outgoingFade); _previewSoftAnimation.Begin();
    }

    private void StartPreviewFocusBlur()
    {
        if (_previewFocusUnavailable || PreviewImageCanvas.ActualWidth <= 0 || PreviewImageCanvas.ActualHeight <= 0) return;
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(PreviewSoftSourcePanel).Compositor;
            if (_previewFocusVisual == null)
            {
                var capabilities = new CompositionCapabilities();
                if (!capabilities.AreEffectsSupported() || !capabilities.AreEffectsFast()) { _previewFocusUnavailable = true; return; }
                _previewFocusSurface = compositor.CreateVisualSurface();
                _previewFocusSurface.SourceVisual = ElementCompositionPreview.GetElementVisual(PreviewSoftSourcePanel);
                _previewFocusSource = compositor.CreateSurfaceBrush(_previewFocusSurface); _previewFocusSource.Stretch = CompositionStretch.None;
                var effect = new GaussianBlurEffect { Name = "Focus", BlurAmount = 4, BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed, Source = new CompositionEffectSourceParameter("Image") };
                _previewFocusFactory = compositor.CreateEffectFactory(effect, new[] { "Focus.BlurAmount" });
                _previewFocusBrush = _previewFocusFactory.CreateBrush(); _previewFocusBrush.SetSourceParameter("Image", _previewFocusSource);
                _previewFocusVisual = compositor.CreateSpriteVisual(); _previewFocusVisual.Brush = _previewFocusBrush;
                ElementCompositionPreview.SetElementChildVisual(PreviewFocusBlurHost, _previewFocusVisual);
            }
            ResizePreviewFocusBlur(); _previewFocusVisual.IsVisible = true;
            _previewFocusBrush!.StopAnimation("Focus.BlurAmount");
            _previewFocusBrush.Properties.InsertScalar("Focus.BlurAmount", 4f);
        }
        catch { _previewFocusUnavailable = true; ResetPreviewFocusBlur(); }
    }

    private void ResizePreviewFocusBlur()
    {
        if (_previewFocusSurface == null || _previewFocusVisual == null) return;
        var size = new Vector2((float)Math.Max(0, PreviewImageCanvas.ActualWidth), (float)Math.Max(0, PreviewImageCanvas.ActualHeight));
        _previewFocusSurface.SourceSize = size; _previewFocusVisual.Size = size;
    }

    private void AnimatePreviewFocusClear()
    {
        if (_previewFocusVisual?.IsVisible != true || _previewFocusBrush == null) return;
        var blur = _previewFocusVisual.Compositor.CreateScalarKeyFrameAnimation();
        blur.Duration = TimeSpan.FromMilliseconds(180); blur.InsertKeyFrame(0, 4); blur.InsertKeyFrame(1, 0);
        _previewFocusBrush.StartAnimation("Focus.BlurAmount", blur);
    }

    private void ResetPreviewFocusBlur()
    {
        _previewSoftAnimation?.Stop(); _previewSoftAnimation = null;
        _previewFocusStarted = 0;
        _previewFocusBrush?.StopAnimation("Focus.BlurAmount");
        if (_previewFocusVisual != null) _previewFocusVisual.IsVisible = false;
    }

    private void SetPreviewPalette(Brush brush, int request)
    {
        _previewPaletteAnimation?.Stop(); _previewPaletteAnimation = null;
        if (!MotionEnabled) { PreviewOverlay.Background = brush; PreviewPaletteTransition.Opacity = 0; PreviewPaletteTransition.Background = null; return; }
        PreviewPaletteTransition.Background = brush; PreviewPaletteTransition.Opacity = 1;
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(320), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, PreviewPaletteTransition); Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard(); storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) =>
        {
            if (request != _previewPaletteRequest) return;
            storyboard.Stop();
            PreviewOverlay.Background = brush; PreviewPaletteTransition.Opacity = 0; PreviewPaletteTransition.Background = null;
            _previewPaletteAnimation = null;
        };
        _previewPaletteAnimation = storyboard; storyboard.Begin();
    }

    private void ClearPreviewPaletteTransition()
    {
        _previewPaletteAnimation?.Stop(); _previewPaletteAnimation = null;
        PreviewPaletteTransition.Opacity = 0; PreviewPaletteTransition.Background = null;
    }

    private void CommitPreviewPaletteTransition()
    {
        if (_previewPaletteAnimation != null && PreviewPaletteTransition.Background is Brush pending) PreviewOverlay.Background = pending;
        ClearPreviewPaletteTransition();
    }
}
