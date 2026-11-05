using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private double? _galleryWheelTarget;
    private bool _galleryWheelRunning;
    private long _galleryWheelFrame;
    private int _galleryWheelDirection;

    private void GalleryWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MainScroll);
        if (point.Properties.IsHorizontalMouseWheel || !ScrollGalleryWheel(point.Properties.MouseWheelDelta)) return;
        e.Handled = true;
    }

    private bool ScrollGalleryWheel(int delta)
    {
        if (delta == 0 || MainScroll.ScrollableHeight <= 0) return false;
        var direction = Math.Sign(delta);
        if (_galleryWheelRunning && direction != _galleryWheelDirection)
            _galleryWheelTarget = MainScroll.VerticalOffset;
        _galleryWheelDirection = direction;
        _galleryWheelTarget = Math.Clamp((_galleryWheelTarget ?? MainScroll.VerticalOffset) - delta, 0, MainScroll.ScrollableHeight);
        if (!MotionEnabled)
        {
            var target = _galleryWheelTarget.Value;
            StopGalleryWheel(); MainScroll.ChangeView(null, target, null, true);
            return true;
        }
        if (!_galleryWheelRunning)
        {
            _galleryWheelRunning = true;
            _galleryWheelFrame = System.Diagnostics.Stopwatch.GetTimestamp();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += GalleryWheelFrame;
        }
        return true;
    }

    private void GalleryWheelFrame(object? sender, object e)
    {
        if (_windowClosing || GallerySurface.Visibility != Visibility.Visible || _galleryWheelTarget == null)
        { StopGalleryWheel(); return; }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = Math.Min(.05, System.Diagnostics.Stopwatch.GetElapsedTime(_galleryWheelFrame, now).TotalSeconds);
        _galleryWheelFrame = now;
        var current = MainScroll.VerticalOffset;
        var target = Math.Clamp(_galleryWheelTarget.Value, 0, MainScroll.ScrollableHeight);
        var remaining = target - current;
        // One running, time-based linear motion. Wheel events update the destination,
        // rather than restarting a new ease-out animation for each notch.
        var step = Math.Min(Math.Abs(remaining), 1440 * elapsed);
        MainScroll.ChangeView(null, current + Math.Sign(remaining) * step, null, true);
        if (Math.Abs(remaining) <= step + .5) StopGalleryWheel();
    }

    private void StopGalleryWheel()
    {
        if (_galleryWheelRunning)
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= GalleryWheelFrame;
        _galleryWheelRunning = false;
        _galleryWheelTarget = null;
        CompleteGalleryScroll();
    }

    private Point? _previewRevealOrigin;
    private FrameworkElement? _indicatorTarget;
    private Storyboard? _previewRippleAnimation;

    private void InitializeInteractionMotion()
    {
        foreach (var target in new FrameworkElement[] { SortCombo, FilterMenuButton })
        {
            target.PointerEntered += (_, _) => MoveFilterIndicator(target);
            target.GotFocus += (_, _) => MoveFilterIndicator(target);
        }
        HeaderControlRow.SizeChanged += (_, _) => { if (_indicatorTarget != null) MoveFilterIndicator(_indicatorTarget, false); };
    }

    private void AnimateActionFeedback(UIElement element)
    {
        if (!MotionEnabled || element is not FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } box) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)box.ActualWidth / 2, (float)box.ActualHeight / 2, 0);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation(); animation.Duration = TimeSpan.FromMilliseconds(220);
        animation.InsertKeyFrame(0, Vector3.One); animation.InsertKeyFrame(.35f, new Vector3(1.1f, 1.1f, 1)); animation.InsertKeyFrame(1, Vector3.One);
        visual.StartAnimation("Scale", animation);
    }

    private void MoveFilterIndicator(FrameworkElement target, bool animate = true)
    {
        if (!target.IsLoaded || target.ActualWidth <= 0) return;
        var point = target.TransformToVisual(FilterIndicatorLayer).TransformPoint(new Point(0, 0));
        var newX = point.X + 10; var newY = point.Y + target.ActualHeight - 2;
        var oldX = Canvas.GetLeft(FilterIndicator); var oldY = Canvas.GetTop(FilterIndicator);
        var hadIndicator = FilterIndicator.Visibility == Visibility.Visible;
        _indicatorTarget = target; FilterIndicator.Width = Math.Max(0, target.ActualWidth - 20);
        Canvas.SetLeft(FilterIndicator, newX); Canvas.SetTop(FilterIndicator, newY); FilterIndicator.Visibility = Visibility.Visible;
        var visual = ElementCompositionPreview.GetElementVisual(FilterIndicator);
        ElementCompositionPreview.SetIsTranslationEnabled(FilterIndicator, true);
        visual.Properties.StopAnimation("Translation"); visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (animate && hadIndicator && MotionEnabled)
            MapVector(visual, "Translation", new Vector3((float)(oldX - newX), (float)(oldY - newY), 0), Vector3.Zero, 190);
    }

    private void StartPreviewReveal(int request)
    {
        var origin = _previewRevealOrigin; _previewRevealOrigin = null;
        ClearPreviewReveal();
        if (!MotionEnabled || origin == null) return;
        PreviewOverlay.UpdateLayout();
        var width = PreviewOverlay.ActualWidth; var height = PreviewOverlay.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var x = Math.Clamp(origin.Value.X, 0, width); var y = Math.Clamp(origin.Value.Y, 0, height);
        var radius = (float)Math.Sqrt(Math.Pow(Math.Max(x, width - x), 2) + Math.Pow(Math.Max(y, height - y), 2)) + 2;
        PreviewRipple.Width = PreviewRipple.Height = radius * 2;
        Canvas.SetLeft(PreviewRipple, x - radius); Canvas.SetTop(PreviewRipple, y - radius);
        PreviewRippleLayer.Visibility = Visibility.Visible;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(_settings.AnimationDurationMs, 180, 360));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var scale = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(scale, PreviewRippleScale); Storyboard.SetTargetProperty(scale, property); storyboard.Children.Add(scale);
        }
        var fade = new DoubleAnimation { From = .65, To = 0, Duration = duration };
        Storyboard.SetTarget(fade, PreviewRipple); Storyboard.SetTargetProperty(fade, "Opacity"); storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => { if (request == _previewVisibilityRequest) ClearPreviewReveal(); };
        _previewRippleAnimation = storyboard; storyboard.Begin();
    }

    private void ClearPreviewReveal()
    {
        _previewRippleAnimation?.Stop(); _previewRippleAnimation = null;
        PreviewRippleLayer.Visibility = Visibility.Collapsed;
    }
}
