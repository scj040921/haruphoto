using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private CompositionVisualSurface? _mapBlurSurface;
    private CompositionSurfaceBrush? _mapBlurSource;
    private CompositionEffectFactory? _mapBlurFactory;
    private CompositionEffectBrush? _mapBlurBrush;
    private SpriteVisual? _mapBlurVisual;
    private bool _mapBlurUnavailable;
    private string? _mapBlurFailure;

    // Sample the map layer itself, not the desktop behind the app. The overlay is
    // used only during a place flight so ordinary map panning stays inexpensive.
    private void StartMapMotionBlur(double milliseconds, float scale, float x, float y)
    {
        if (_mapBlurUnavailable || MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0) return;
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(MapTiles).Compositor;
            if (_mapBlurVisual == null)
            {
                var capabilities = new CompositionCapabilities();
                if (!capabilities.AreEffectsSupported() || !capabilities.AreEffectsFast()) { _mapBlurUnavailable = true; _mapBlurFailure = "Composition effects unavailable or slow"; return; }
                _mapBlurSurface = compositor.CreateVisualSurface();
                _mapBlurSurface.SourceVisual = ElementCompositionPreview.GetElementVisual(MapTiles);
                _mapBlurSource = compositor.CreateSurfaceBrush(_mapBlurSurface);
                _mapBlurSource.Stretch = CompositionStretch.None;
                var effect = new GaussianBlurEffect { Name = "MapMotionBlur", BlurAmount = 0, BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed, Source = new CompositionEffectSourceParameter("Map") };
                _mapBlurFactory = compositor.CreateEffectFactory(effect, new[] { "MapMotionBlur.BlurAmount" });
                _mapBlurBrush = _mapBlurFactory.CreateBrush();
                _mapBlurBrush.SetSourceParameter("Map", _mapBlurSource);
                _mapBlurVisual = compositor.CreateSpriteVisual();
                _mapBlurVisual.Brush = _mapBlurBrush;
                ElementCompositionPreview.SetElementChildVisual(MapBlurHost, _mapBlurVisual);
            }
            var size = new Vector2((float)MapViewport.ActualWidth, (float)MapViewport.ActualHeight);
            _mapBlurSurface!.SourceSize = size; _mapBlurVisual.Size = size;
            _mapBlurVisual.CenterPoint = new Vector3(size / 2, 0);
            _mapBlurVisual.IsVisible = true;
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .7f), new Vector2(.2f, 1));
            var zoom = compositor.CreateVector3KeyFrameAnimation(); zoom.Duration = duration;
            zoom.InsertKeyFrame(0, Vector3.One); zoom.InsertKeyFrame(1, new Vector3(scale, scale, 1), easing);
            var pan = compositor.CreateVector3KeyFrameAnimation(); pan.Duration = duration;
            pan.InsertKeyFrame(0, Vector3.Zero); pan.InsertKeyFrame(1, new Vector3(x, y, 0), easing);
            var strength = Math.Clamp(7f + (MathF.Sqrt(x * x + y * y) + MathF.Abs(scale - 1) * 80) / (float)Math.Max(100, milliseconds) * 4, 8f, 16f);
            var blur = compositor.CreateScalarKeyFrameAnimation(); blur.Duration = duration;
            blur.InsertKeyFrame(0, 0); blur.InsertKeyFrame(.22f, strength); blur.InsertKeyFrame(.7f, strength * .65f); blur.InsertKeyFrame(1, 0);
            var opacity = compositor.CreateScalarKeyFrameAnimation(); opacity.Duration = duration;
            opacity.InsertKeyFrame(0, 0); opacity.InsertKeyFrame(.16f, 1); opacity.InsertKeyFrame(.75f, 1); opacity.InsertKeyFrame(1, 0);
            _mapBlurVisual.StartAnimation("Scale", zoom); _mapBlurVisual.StartAnimation("Offset", pan);
            _mapBlurBrush!.StartAnimation("MapMotionBlur.BlurAmount", blur); _mapBlurVisual.StartAnimation("Opacity", opacity);
        }
        catch (Exception ex)
        {
            // Devices without supported composition effects still get the camera flight.
            _mapBlurUnavailable = true; _mapBlurFailure = ex.Message; ResetMapMotionBlur();
        }
    }

    private void ResetMapMotionBlur()
    {
        if (_mapBlurVisual == null) return;
        _mapBlurVisual.StopAnimation("Scale"); _mapBlurVisual.Scale = Vector3.One;
        _mapBlurVisual.StopAnimation("Offset"); _mapBlurVisual.Offset = Vector3.Zero;
        _mapBlurVisual.StopAnimation("Opacity"); _mapBlurVisual.Opacity = 0; _mapBlurVisual.IsVisible = false;
        _mapBlurBrush?.StopAnimation("MapMotionBlur.BlurAmount");
    }
}
