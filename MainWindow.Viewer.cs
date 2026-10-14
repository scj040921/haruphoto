using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private bool _previewFullscreen, _filmstripHidden;
    private AppWindowPresenter? _previewPreviousPresenter;
    private uint? _previewDragId;
    private Point _previewDragStart;
    private double _previewDragX, _previewDragY;

    private void InitializeViewerGestures()
    {
        // 拦截 ScrollViewer 的默认滚轮滚动；普通滚轮直接以指针为中心缩放。
        PreviewImageCanvas.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(PreviewWheelChanged), true);
        PreviewImageCanvas.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(PreviewDragPressed), true);
        PreviewImageScroll.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(PreviewDragMoved), true);
        PreviewImageScroll.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, _) => EndPreviewDrag()), true);
        PreviewImageScroll.PointerCaptureLost += (_, _) => { _previewDragId = null; };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) EndPreviewDrag(); };
    }

    private void PreviewWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PreviewImageScroll);
        if (point.Properties.IsHorizontalMouseWheel) return;
        EndPreviewDrag();
        ZoomPreviewAt(point.Position, (float)Math.Pow(1.2, point.Properties.MouseWheelDelta / 120d));
        e.Handled = true;
    }

    private void ZoomPreviewAt(Point pointer, float multiplier)
    {
        var change = ViewerGeometry.ZoomAt(PreviewImageScroll.HorizontalOffset, PreviewImageScroll.VerticalOffset,
            PreviewImageScroll.ZoomFactor, pointer.X, pointer.Y, multiplier, PreviewImageCanvas.ActualWidth, PreviewImageCanvas.ActualHeight);
        PreviewImageScroll.ChangeView(change.X, change.Y, change.Zoom, true);
    }

    private void PreviewDragPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PreviewImageScroll);
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed || PreviewImageScroll.ZoomFactor <= 1) return;
        _previewDragStart = point.Position;
        _previewDragX = PreviewImageScroll.HorizontalOffset; _previewDragY = PreviewImageScroll.VerticalOffset;
        if (PreviewImageScroll.CapturePointer(e.Pointer)) _previewDragId = e.Pointer.PointerId;
        e.Handled = true;
    }

    private void PreviewDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_previewDragId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(PreviewImageScroll);
        if (!point.Properties.IsLeftButtonPressed) { EndPreviewDrag(); return; }
        PanPreviewTo(point.Position);
        e.Handled = true;
    }

    private void PanPreviewTo(Point point) => PreviewImageScroll.ChangeView(
        Math.Clamp(_previewDragX - point.X + _previewDragStart.X, 0, PreviewImageScroll.ScrollableWidth),
        Math.Clamp(_previewDragY - point.Y + _previewDragStart.Y, 0, PreviewImageScroll.ScrollableHeight), null, true);

    private void EndPreviewDrag()
    {
        if (_previewDragId == null) return;
        _previewDragId = null; PreviewImageScroll.ReleasePointerCaptures();
    }

    private void PreviewFullscreen_Click(object sender, RoutedEventArgs e) => SetPreviewFullscreen(!_previewFullscreen);

    private void SetPreviewFullscreen(bool fullscreen)
    {
        if (_previewFullscreen == fullscreen) return;
        EndPreviewDrag();
        if (fullscreen) { _previewPreviousPresenter = AppWindow.Presenter; AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); }
        else if (_previewPreviousPresenter != null) { AppWindow.SetPresenter(_previewPreviousPresenter); _previewPreviousPresenter = null; }
        _previewFullscreen = fullscreen;
        PreviewToolbar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        PreviewFullscreenExit.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = !fullscreen && _previewInfoVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = !fullscreen && _previewInfoVisible ? new GridLength(310) : new GridLength(0);
        ApplyFilmstripVisibility();
        DispatcherQueue.TryEnqueue(() => { if (!_windowClosing) ApplyFitPreviewZoom(); });
    }

    private void PreviewFilmstripToggle_Click(object sender, RoutedEventArgs e)
    {
        _filmstripHidden = !_filmstripHidden;
        ApplyFilmstripVisibility();
        if (!_filmstripHidden)
            foreach (var photo in _filmstripContainers.Values.Distinct().ToArray())
                if (_filmstripCts != null) _ = EnsureFilmstripThumbnailAsync(photo, _filmstripCts.Token);
    }

    private void ApplyFilmstripVisibility()
    {
        var visible = !_filmstripHidden && !_previewFullscreen;
        PreviewFilmstripBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewFilmstripBar.Opacity = visible && _settings.FilmstripMode != 1 ? 1 : 0;
        PreviewFilmstripBar.IsHitTestVisible = visible && _settings.FilmstripMode != 1;
        PreviewImageStage.Margin = new Thickness(12, 20, 12, visible && _settings.FilmstripMode != 1 ? 100 : 20);
        PreviewFilmstripToggleBtn.Content = _filmstripHidden ? "显示胶片条" : "隐藏胶片条";
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();
    private bool _removalDialogOpen;
    private async Task<bool> ConfirmLibraryRemovalAsync(IReadOnlyCollection<PhotoItem> targets, string title)
    {
        if (_removalDialogOpen || targets.Count == 0) return false;
        _removalDialogOpen = true;
        try
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog { Title = title, Content = $"将 {targets.Count} 张照片从图库移除？\n只移除图库记录，不删除原文件。可在底栏撤销。", PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            return await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
        }
        catch (Exception ex) { if (!_windowClosing) StatusText.Text = "移除未完成：" + ex.Message; return false; }
        finally { _removalDialogOpen = false; }
    }

    private void RemoveLibraryRecords(IEnumerable<PhotoItem> photos)
    {
        var removed = photos.Where(_allPhotos.Contains).Distinct().ToArray();
        var paths = removed.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allPhotos.RemoveAll(p => paths.Contains(p.FilePath));
        foreach (var photo in removed) { _selectedPaths.Remove(photo.FilePath); photo.IsSelected = false; }
        RegisterUndo($"移除 {removed.Length} 张照片", () =>
        {
            var existing = _allPhotos.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var photo in removed) if (existing.Add(photo.FilePath)) _allPhotos.Add(photo);
            RebuildCategories();
        });
        RebuildCategories();
    }
}
