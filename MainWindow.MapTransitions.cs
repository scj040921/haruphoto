using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using Windows.Foundation;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _mapPlaceTransitionCts;
    private int _mapPlaceTransitionRequest;
    private bool _mapPlaceEntranceActive, _mapPlaceClosing;
    private (double Latitude, double Longitude, int Zoom)? _mapPlaceReturnCamera;
    private (double Latitude, double Longitude) _mapPlaceAnchor;
    private Rect? _mapPlaceOpeningSource;
    private Rect? _mapPhotoPreviewSource;
    private GridViewItem? _mapPhotoPreviewSourceElement;
    private PhotoItem? _mapPhotoPreviewPhoto;
    private ImageSource? _mapPhotoPreviewImageSource;
    private bool _mapPhotoPreviewClosing;
    private bool _mapPhotoPreviewReturnRunning;
    private Button? _mapHeroSourceMarker;

    private Rect? CaptureMapPhotoPreviewSource(PhotoItem photo)
    {
        var index = _mapPlace.IndexOf(photo);
        var container = index >= 0 ? MapPlacePhotos.ContainerFromIndex(index) as GridViewItem : null;
        var image = container == null ? null : MapDescendant<Image>(container);
        var frame = image == null ? null : VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(image)) as FrameworkElement;
        var root = Content as UIElement;
        if (frame == null || root == null || frame.ActualWidth <= 0 || frame.ActualHeight <= 0) return null;
        _mapPhotoPreviewSourceElement = container;
        _mapPhotoPreviewPhoto = photo;
        _mapPhotoPreviewImageSource = image?.Source ?? photo.ThumbnailSource;
        var point = frame.TransformToVisual(root).TransformPoint(new Point());
        return new Rect(point.X, point.Y, frame.ActualWidth, frame.ActualHeight);
    }

    private static Rect ContainedMapPhotoRect(Rect bounds, double aspect)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(aspect) || aspect <= 0)
            return bounds;
        var fittedWidth = bounds.Width;
        var fittedHeight = fittedWidth / aspect;
        if (fittedHeight > bounds.Height)
        {
            fittedHeight = bounds.Height;
            fittedWidth = fittedHeight * aspect;
        }
        return new Rect(bounds.X + (bounds.Width - fittedWidth) / 2,
            bounds.Y + (bounds.Height - fittedHeight) / 2, fittedWidth, fittedHeight);
    }

    private double MapPreviewPhotoAspect
    {
        get
        {
            if (_mapPhotoPreviewImageSource is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                return (double)bitmap.PixelWidth / bitmap.PixelHeight;
            return _mapPhotoPreviewSource is Rect source && source.Height > 0 ? source.Width / source.Height : 1;
        }
    }

    private Rect MapPreviewImageBounds(UIElement root, double? aspectOverride = null)
    {
        var origin = PreviewImageScroll.TransformToVisual(root).TransformPoint(new Point());
        return ContainedMapPhotoRect(new Rect(origin.X, origin.Y, PreviewImageScroll.ActualWidth, PreviewImageScroll.ActualHeight), aspectOverride ?? MapPreviewPhotoAspect);
    }

    private void SetMapPhotoHero(Rect source)
    {
        var aspect = MapPreviewPhotoAspect;
        var imageRect = ContainedMapPhotoRect(source, aspect);
        MapPhotoHeroFrame.Width = imageRect.Width; MapPhotoHeroFrame.Height = imageRect.Height;
        Canvas.SetLeft(MapPhotoHeroFrame, 0); Canvas.SetTop(MapPhotoHeroFrame, 0);
        MapPhotoHeroImage.Source = _mapPhotoPreviewImageSource;
        var visual = MapVisual(MapPhotoHeroFrame);
        visual.CenterPoint = new Vector3((float)imageRect.Width / 2, (float)imageRect.Height / 2, 0);
        visual.Scale = Vector3.One; visual.Properties.InsertVector3("Translation", Vector3.Zero);
        MapPhotoHeroLayer.Visibility = Visibility.Visible;
    }

    private async Task AnimateMapPhotoHeroAsync(Rect from, Rect to, double? aspectOverride = null)
    {
        var imageAspect = aspectOverride ?? MapPreviewPhotoAspect;
        from = ContainedMapPhotoRect(from, imageAspect);
        to = ContainedMapPhotoRect(to, imageAspect);
        if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0) return;
        var frame = MapVisual(MapPhotoHeroFrame);
        frame.CenterPoint = new Vector3((float)MapPhotoHeroFrame.Width / 2, (float)MapPhotoHeroFrame.Height / 2, 0);
        var fromCenter = new Vector3((float)(from.X + from.Width / 2), (float)(from.Y + from.Height / 2), 0);
        var toCenter = new Vector3((float)(to.X + to.Width / 2), (float)(to.Y + to.Height / 2), 0);
        var fromScale = new Vector3((float)(from.Width / MapPhotoHeroFrame.Width), (float)(from.Height / MapPhotoHeroFrame.Height), 1);
        var toScale = new Vector3((float)(to.Width / MapPhotoHeroFrame.Width), (float)(to.Height / MapPhotoHeroFrame.Height), 1);
        var fromTranslation = fromCenter - frame.CenterPoint;
        var toTranslation = toCenter - frame.CenterPoint;
        // Put the visual at the real first frame before starting the compositor animation.
        // This avoids the one-frame jump from Canvas origin, especially on reverse transitions.
        frame.Scale = fromScale;
        frame.Properties.InsertVector3("Translation", fromTranslation);
        var duration = MapTransitionDuration;
        using var batch = frame.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var completed = new TaskCompletionSource<bool>(); batch.Completed += (_, _) => completed.TrySetResult(true);
        var easing = frame.Compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, .82f), new Vector2(.24f, 1));
        var scale = frame.Compositor.CreateVector3KeyFrameAnimation(); scale.Duration = TimeSpan.FromMilliseconds(duration);
        scale.InsertKeyFrame(0, fromScale);
        scale.InsertKeyFrame(1, toScale, easing); frame.StartAnimation("Scale", scale);
        var movement = frame.Compositor.CreateVector3KeyFrameAnimation(); movement.Duration = scale.Duration;
        movement.InsertKeyFrame(0, fromTranslation);
        movement.InsertKeyFrame(1, toTranslation, easing);
        frame.Properties.StartAnimation("Translation", movement);
        batch.End(); await completed.Task;
    }

    private void RestoreMapPhotoPreviewSource()
    {
        if (_mapPhotoPreviewSourceElement != null)
        {
            ResetMapVisual(_mapPhotoPreviewSourceElement);
            _mapPhotoPreviewSourceElement = null;
        }
    }

    private async Task OpenMapPhotoPreviewTransitionAsync(Rect source)
    {
        try
        {
            PreviewOverlay.UpdateLayout();
            var root = Content as UIElement;
            if (root == null || PreviewImageScroll.ActualWidth <= 0 || PreviewImageScroll.ActualHeight <= 0 || _mapPhotoPreviewImageSource == null) return;
            if (_mapPhotoPreviewSourceElement != null) MapVisual(_mapPhotoPreviewSourceElement).Opacity = 0;
            SetMapPhotoHero(source);
            MapVisual(PreviewImageStage).Opacity = 0;
            MapFade(MapVisual(PreviewContentRoot), 0, 1, MapTransitionDuration * .9);
            MapFade(MapVisual(PreviewOverlay), 0, 1, MapTransitionDuration * .85);
            await AnimateMapPhotoHeroAsync(source, MapPreviewImageBounds(root));
            var thumbnailDeadline = Environment.TickCount64 + 1400;
            while (_previewIsLoading && PreviewSoftImage.Source == null && Environment.TickCount64 < thumbnailDeadline)
                await Task.Delay(16);
            MapFade(MapVisual(MapPhotoHeroFrame), 1, 0, 100);
            MapFade(MapVisual(PreviewImageStage), 0, 1, 100);
            await Task.Delay(110);
            MapPhotoHeroLayer.Visibility = Visibility.Collapsed; MapPhotoHeroImage.Source = null;
            ResetMapVisual(MapPhotoHeroFrame); ResetMapVisual(PreviewImageStage);
            ResetMapVisual(PreviewContentRoot); ResetMapVisual(PreviewOverlay);
            PreviewContentRoot.Opacity = 1; PreviewOverlay.Opacity = 1;
        }
        catch (Exception ex) { if (!_windowClosing) MapStatus.Text = "照片转场未完成：" + ex.Message; }
        finally
        {
            MapPhotoHeroLayer.Visibility = Visibility.Collapsed; MapPhotoHeroImage.Source = null;
            ResetMapVisual(PreviewImageStage);
            PreviewOverlay.Opacity = 1; ResetMapVisual(PreviewContentRoot); ResetMapVisual(PreviewOverlay);
            PreviewContentRoot.Opacity = 1;
            RestoreMapPhotoPreviewSource();
        }
    }

    private async Task CloseMapPhotoPreviewTransitionAsync(Rect source)
    {
        if (_mapPhotoPreviewReturnRunning) return;
        _mapPhotoPreviewReturnRunning = true;
        try
        {
            var root = Content as UIElement;
            if (root == null || PreviewImageScroll.ActualWidth <= 0 || PreviewImageScroll.ActualHeight <= 0 || _mapPhotoPreviewImageSource == null) return;
            if (_mapPhotoPreviewSourceElement != null) MapVisual(_mapPhotoPreviewSourceElement).Opacity = 0;
            var currentImage = _currentView.Count > _previewIndex && _previewIndex >= 0 ? _currentView[_previewIndex] : _mapPhotoPreviewPhoto;
            MapPhotoHeroImage.Source = currentImage?.ThumbnailSource ?? _mapPhotoPreviewImageSource;
            var currentAspect = MapPreviewPhotoAspect;
            if (currentImage?.ThumbnailSource is BitmapSource currentBitmap && currentBitmap.PixelWidth > 0 && currentBitmap.PixelHeight > 0)
                currentAspect = (double)currentBitmap.PixelWidth / currentBitmap.PixelHeight;
            var frameRect = ContainedMapPhotoRect(source, currentAspect);
            var currentBounds = MapPreviewImageBounds(root, currentAspect);
            MapPhotoHeroFrame.Width = currentBounds.Width; MapPhotoHeroFrame.Height = currentBounds.Height;
            Canvas.SetLeft(MapPhotoHeroFrame, 0); Canvas.SetTop(MapPhotoHeroFrame, 0);
            var frameVisual = MapVisual(MapPhotoHeroFrame); frameVisual.CenterPoint = new Vector3((float)MapPhotoHeroFrame.Width / 2, (float)MapPhotoHeroFrame.Height / 2, 0);
            frameVisual.Scale = Vector3.One; frameVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            MapPhotoHeroLayer.Visibility = Visibility.Visible;
            MapFade(MapVisual(PreviewOverlay), 1, 0, MapTransitionDuration * .85);
            await AnimateMapPhotoHeroAsync(currentBounds, frameRect, currentAspect);
            MapFade(MapVisual(MapPhotoHeroFrame), 1, 0, 100); await Task.Delay(110);
            MapPhotoHeroLayer.Visibility = Visibility.Collapsed; MapPhotoHeroImage.Source = null;
        }
        catch (Exception ex) { if (!_windowClosing) MapStatus.Text = "照片返回转场未完成：" + ex.Message; }
        finally
        {
            _mapPhotoPreviewClosing = true;
            _mapPhotoPreviewSource = null;
            ClosePreview();
            _mapPhotoPreviewClosing = false;
            _mapPhotoPreviewReturnRunning = false;
            RestoreMapPhotoPreviewSource();
        }
    }

    private static Visual MapVisual(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }

    private static void ResetMapVisual(UIElement element)
    {
        var visual = MapVisual(element);
        visual.StopAnimation("Opacity"); visual.StopAnimation("Scale");
        visual.Properties.StopAnimation("Translation");
        visual.Opacity = 1; visual.Scale = Vector3.One;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private double MapTransitionDuration => Math.Clamp(_settings.AnimationDurationMs * .8, 220, 320);

    private static void MapFade(Visual visual, float from, float to, double duration, double delay = 0)
    {
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(duration); animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        animation.InsertKeyFrame(0, from); animation.InsertKeyFrame(1, to);
        visual.Opacity = from; visual.StartAnimation("Opacity", animation);
    }

    private static void MapVector(Visual visual, string property, Vector3 from, Vector3 to, double duration, double delay = 0)
    {
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(duration); animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, .82f), new Vector2(.24f, 1));
        animation.InsertKeyFrame(0, from); animation.InsertKeyFrame(1, to, easing);
        if (property == "Translation")
        {
            visual.Properties.InsertVector3(property, from); visual.Properties.StartAnimation(property, animation);
        }
        else { visual.Scale = from; visual.StartAnimation(property, animation); }
    }

    private async Task MapAnimationBatchAsync(Action animate, CancellationToken token)
    {
        using var batch = MapVisual(MapTransitionHero).Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var complete = new TaskCompletionSource<bool>();
        batch.Completed += (_, _) => complete.TrySetResult(true);
        animate(); batch.End(); await complete.Task.WaitAsync(token);
    }

    private void AnimateMapArrival()
    {
        ResetMapVisual(MapHeader); ResetMapVisual(MapViewport);
        if (!MotionEnabled) return;
        MapFade(MapVisual(MapViewport), 0, 1, 220);
        MapFade(MapVisual(MapHeader), 0, 1, 200);
        MapVector(MapVisual(MapHeader), "Translation", new Vector3(0, -10, 0), Vector3.Zero, 260);
    }

    private (int Request, CancellationToken Token) BeginMapPlaceTransition()
    {
        CancelMapPlaceTransition();
        _mapPlaceTransitionCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        return (_mapPlaceTransitionRequest, _mapPlaceTransitionCts.Token);
    }

    private void CancelMapPlaceTransition()
    {
        ++_mapPlaceTransitionRequest;
        _mapPlaceTransitionCts?.Cancel(); _mapPlaceTransitionCts?.Dispose(); _mapPlaceTransitionCts = null;
        _mapPlaceEntranceActive = _mapPlaceClosing = false;
        ResetMapVisual(MapPlaceOverlay); ResetMapVisual(MapPlaceHeader); ResetMapVisual(MapHeader);
        foreach (var container in _mapPhotoContainers.Keys.OfType<UIElement>()) ResetMapVisual(container);
        HideMapHero();
    }

    private void HideMapHero()
    {
        if (_mapHeroSourceMarker != null) { _mapHeroSourceMarker.Opacity = 1; _mapHeroSourceMarker = null; }
        ResetMapVisual(MapTransitionHero);
        foreach (var element in new[] { MapHeroBack, MapHeroMiddle })
        {
            ResetMapVisual(element); var visual = MapVisual(element);
            visual.StopAnimation("RotationAngleInDegrees"); visual.RotationAngleInDegrees = 0;
        }
        MapTransitionHero.Visibility = Visibility.Collapsed;
        MapHeroImage.Source = MapHeroMiddleImage.Source = MapHeroBackImage.Source = null;
    }

    private static T? MapDescendant<T>(DependencyObject root, bool last = false) where T : DependencyObject
    {
        if (root is T match) return match;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (MapDescendant<T>(VisualTreeHelper.GetChild(root, last ? count - 1 - i : i), last) is T found) return found;
        return null;
    }

    private Rect MapPhotoDestination(out GridViewItem? container)
    {
        MapPlacePhotos.UpdateLayout();
        container = MapPlacePhotos.ContainerFromIndex(0) as GridViewItem;
        var image = container == null ? null : MapDescendant<Image>(container);
        var frame = image == null ? null : VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(image)) as FrameworkElement;
        if (frame != null && frame.ActualWidth > 0 && frame.ActualHeight > 0)
        {
            var point = frame.TransformToVisual(MapTransitionLayer).TransformPoint(new Point());
            return new Rect(point.X, point.Y, frame.ActualWidth, frame.ActualHeight);
        }
        return new Rect(32, MapPlaceHeader.ActualHeight + 52, 196, 148);
    }

    private Rect MapMarkerSource(List<PhotoItem>? photos = null)
    {
        var sourcePhotos = photos ?? _mapPlace;
        if (sourcePhotos.Count > 0)
        {
            var photo = sourcePhotos[0];
            var key = string.IsNullOrEmpty(photo.MapPlaceId) ? $"{photo.MapLatitude}:{photo.MapLongitude}" : photo.MapPlaceId;
            if (_mapMarkerViews.TryGetValue(key, out var marker))
            {
                MapMarkers.UpdateLayout();
                var image = MapDescendant<Image>(marker.Button, last: true);
                var card = image == null ? null : VisualTreeHelper.GetParent(image) as FrameworkElement;
                if (card != null && card.ActualWidth > 0 && card.ActualHeight > 0)
                {
                    var point = card.TransformToVisual(MapTransitionLayer).TransformPoint(new Point());
                    return new Rect(point.X, point.Y, card.ActualWidth, card.ActualHeight);
                }
            }
        }
        // 地点刚被移除或未实现时，仍可沿打开时保存的位置完成收回。
        var center = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom);
        var anchor = MapGeometry.Project(_mapPlaceAnchor.Latitude, _mapPlaceAnchor.Longitude, _mapZoom);
        var viewportOrigin = MapViewport.TransformToVisual(MapTransitionLayer).TransformPoint(new Point());
        return new Rect(viewportOrigin.X + MapViewport.ActualWidth / 2 + MapGeometry.WrappedDelta(anchor.X, center.X, _mapZoom) - 34,
            viewportOrigin.Y + MapViewport.ActualHeight / 2 + anchor.Y - center.Y - 75, 68, 56);
    }

    private void PrepareMapHero(Rect destination)
    {
        MapTransitionHero.Width = destination.Width; MapTransitionHero.Height = destination.Height;
        Canvas.SetLeft(MapTransitionHero, destination.X); Canvas.SetTop(MapTransitionHero, destination.Y);
        MapHeroImage.Source = _mapPlace[0].ThumbnailSource;
        MapHeroMiddleImage.Source = _mapPlace[Math.Min(1, _mapPlace.Count - 1)].ThumbnailSource;
        MapHeroBackImage.Source = _mapPlace[Math.Min(2, _mapPlace.Count - 1)].ThumbnailSource;
        MapTransitionHero.Visibility = Visibility.Visible;
        var visual = MapVisual(MapTransitionHero);
        visual.CenterPoint = new Vector3((float)destination.Width / 2, (float)destination.Height / 2, 0);
    }

    private void AnimateMapHero(Rect destination, Rect source, bool opening, double duration)
    {
        var visual = MapVisual(MapTransitionHero);
        var small = new Vector3((float)(source.Width / destination.Width), (float)(source.Height / destination.Height), 1);
        var offset = new Vector3((float)(source.X + source.Width / 2 - destination.X - destination.Width / 2),
            (float)(source.Y + source.Height / 2 - destination.Y - destination.Height / 2), 0);
        MapVector(visual, "Scale", opening ? small : Vector3.One, opening ? Vector3.One : small, duration);
        MapVector(visual, "Translation", opening ? offset : Vector3.Zero, opening ? Vector3.Zero : offset, duration);
        foreach (var (element, angle) in new[] { (MapHeroBack, 9f), (MapHeroMiddle, -5f) })
        {
            var back = MapVisual(element); back.CenterPoint = visual.CenterPoint;
            var turn = back.Compositor.CreateScalarKeyFrameAnimation(); turn.Duration = TimeSpan.FromMilliseconds(duration);
            turn.InsertKeyFrame(0, opening ? angle : 0); turn.InsertKeyFrame(1, opening ? 0 : angle);
            back.StartAnimation("RotationAngleInDegrees", turn);
            var fan = back.Compositor.CreateVector3KeyFrameAnimation(); fan.Duration = TimeSpan.FromMilliseconds(duration);
            var spread = new Vector3(angle > 0 ? 34 : -34, angle > 0 ? 16 : -9, 0);
            fan.InsertKeyFrame(0, Vector3.Zero); fan.InsertKeyFrame(.42f, spread); fan.InsertKeyFrame(1, Vector3.Zero);
            back.Properties.StartAnimation("Translation", fan);
            MapFade(back, opening ? 1 : 0, opening ? 0 : 1, duration * .8);
        }
    }

    private async Task OpenMapPlaceTransitionAsync(int request, CancellationToken token)
    {
        GridViewItem? first = null;
        try
        {
            if (!MotionEnabled) { _mapPlaceOpeningSource = null; return; }
            await Task.Yield();
            // Start from the available marker image; decoding must never delay input feedback.
            if (_mapPlace[0].ThumbnailSource == null)
                _ = EnsureFilmstripThumbnailAsync(_mapPlace[0], _mapThumbnailLifetime.Token);
            token.ThrowIfCancellationRequested();
            var source = _mapPlaceOpeningSource ?? MapMarkerSource();
            _mapPlaceOpeningSource = null;
            var destination = MapPhotoDestination(out first);
            var hasHero = _mapPlace[0].ThumbnailSource != null;
            if (hasHero) { PrepareMapHero(destination); if (first != null) { ResetMapVisual(first); MapVisual(first).Opacity = 0; } }
            if (hasHero)
            {
                var photo = _mapPlace[0];
                var key = string.IsNullOrEmpty(photo.MapPlaceId) ? $"{photo.MapLatitude}:{photo.MapLongitude}" : photo.MapPlaceId;
                if (_mapMarkerViews.TryGetValue(key, out var marker))
                { _mapHeroSourceMarker = marker.Button; marker.Button.Opacity = 0; }
            }
            var duration = MapTransitionDuration;
            await MapAnimationBatchAsync(() =>
            {
                MapFade(MapVisual(MapHeader), 1, 0, duration * .3);
                MapFade(MapVisual(MapPlaceOverlay), 0, 1, duration, 35);
                MapVector(MapVisual(MapPlaceHeader), "Translation", new Vector3(0, 16, 0), Vector3.Zero, duration);
                if (hasHero) AnimateMapHero(destination, source, true, duration);
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_windowClosing) MapStatus.Text = "地点展开未完成：" + ex.Message; }
        finally
        {
            if (request == _mapPlaceTransitionRequest)
            {
                _mapPlaceEntranceActive = false; HideMapHero();
                ResetMapVisual(MapPlaceOverlay); ResetMapVisual(MapPlaceHeader);
                if (first != null) ResetMapVisual(first);
            }
        }
    }

    private async Task CloseMapPlaceTransitionAsync()
    {
        if (_mapPlaceClosing || MapPlaceOverlay.Visibility != Visibility.Visible) return;
        _mapPlaceOpeningSource = null;
        var (request, token) = BeginMapPlaceTransition(); _mapPlaceClosing = true;
        try
        {
            if (MotionEnabled && _mapPlace.Count > 0)
            {
                var destination = MapPhotoDestination(out var first);
                var hasHero = _mapPlace[0].ThumbnailSource != null;
                if (hasHero) { PrepareMapHero(destination); if (first != null) { ResetMapVisual(first); MapVisual(first).Opacity = 0; } }
                var duration = MapTransitionDuration * .8;
                await MapAnimationBatchAsync(() =>
                {
                    MapFade(MapVisual(MapPlaceOverlay), 1, 0, duration * .8);
                    MapVector(MapVisual(MapPlaceHeader), "Translation", Vector3.Zero, new Vector3(0, 12, 0), duration);
                    if (hasHero) AnimateMapHero(destination, MapMarkerSource(), false, duration);
                }, token);
            }
            token.ThrowIfCancellationRequested();
            if (request != _mapPlaceTransitionRequest) return;
            MapPlaceOverlay.Visibility = Visibility.Collapsed; MapPlacePhotos.ItemsSource = null; MapPlaceHeroBackdrop.Source = null;
            RestoreMapPlaceHud();
            _mapPhotoContainers.Clear(); _mapPlace.Clear(); HideMapHero(); TrimThumbnailMemory();
            if (_mapPlaceReturnCamera is { } camera)
            {
                _mapPlaceReturnCamera = null;
                _mapLatitude = camera.Latitude; _mapLongitude = camera.Longitude; _mapZoom = camera.Zoom;
                await AnimateMapMotionAsync(Math.Clamp(_settings.AnimationDurationMs * .65, 140, 360));
            }
            else _ = RenderMapAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_windowClosing) MapStatus.Text = "返回地图未完成：" + ex.Message; }
        finally
        {
            if (request == _mapPlaceTransitionRequest)
            {
                _mapPlaceClosing = false; HideMapHero(); ResetMapVisual(MapPlaceOverlay); ResetMapVisual(MapPlaceHeader);
            }
        }
    }

    private void AnimateMapPhotoArrival(UIElement container, int index)
    {
        if (!_mapPlaceEntranceActive || !MotionEnabled || index > 11) return;
        var visual = MapVisual(container); visual.CenterPoint = new Vector3(100, 75, 0);
        var delay = Math.Min(index, 7) * 32;
        MapFade(visual, 0, 1, MapTransitionDuration * .8, delay);
        MapVector(visual, "Scale", new Vector3(.86f, .86f, 1), Vector3.One, MapTransitionDuration, delay);
        MapVector(visual, "Translation", new Vector3(-18, 32, 0), Vector3.Zero, MapTransitionDuration, delay);
    }
}
