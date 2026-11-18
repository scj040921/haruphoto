using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private bool _previewFullscreen, _filmstripHidden, _filmstripPinnedVisible;
    private bool _filmstripVertical;
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
        ApplyResponsivePreviewDetail();
        ApplyFilmstripVisibility();
        DispatcherQueue.TryEnqueue(() => { if (!_windowClosing) ApplyFitPreviewZoom(); });
    }

    private void PreviewFilmstripToggle_Click(object sender, RoutedEventArgs e)
    {
        var currentlyShown = PreviewFilmstripBar.Visibility == Visibility.Visible && PreviewFilmstripBar.Opacity > .5;
        _filmstripHidden = currentlyShown;
        _filmstripPinnedVisible = !currentlyShown;
        ApplyFilmstripVisibility();
        if (!_filmstripHidden)
            foreach (var photo in _filmstripContainers.Values.Distinct().ToArray())
                if (_filmstripCts != null) _ = EnsureFilmstripThumbnailAsync(photo, _filmstripCts.Token);
    }

    private void ApplyFilmstripVisibility()
    {
        var visible = !_filmstripHidden && !_previewFullscreen;
        PreviewFilmstripColumn.Width = new GridLength(visible && _filmstripVertical && _settings.FilmstripMode != 1 ? 120 : 0);
        PreviewFilmstripBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var shown = visible && (_settings.FilmstripMode != 1 || _filmstripPinnedVisible);
        PreviewFilmstripBar.Opacity = shown ? 1 : 0;
        PreviewFilmstripBar.IsHitTestVisible = shown;
        PreviewImageStage.Margin = new Thickness(12, 20, 12, !_filmstripVertical && visible && _settings.FilmstripMode != 1 ? 100 : 20);
        PreviewFilmstripToggleBtn.Content = shown ? "隐藏胶片条" : "显示胶片条";
        ApplyResponsivePreviewDetail();
    }

    private void ConfigureFilmstripLayout(bool vertical)
    {
        var changed = _filmstripVertical != vertical;
        _filmstripVertical = vertical;
        var overlay = vertical && _settings.FilmstripMode == 1;
        Grid.SetColumn(PreviewFilmstripBar, overlay ? 2 : vertical ? 0 : 1);
        Grid.SetColumnSpan(PreviewFilmstripBar, vertical ? 1 : 3);
        PreviewFilmstripBar.HorizontalAlignment = overlay ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        PreviewFilmstripBar.Width = overlay ? 120 : double.NaN;
        PreviewFilmstripBar.Height = vertical ? double.NaN : 90;
        PreviewFilmstripBar.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        PreviewFilmstrip.Height = vertical ? double.NaN : 78;
        PreviewFilmstrip.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        if (!changed) return;
        PreviewFilmstrip.ItemsPanel = (ItemsPanelTemplate)PreviewContentRoot.Resources[vertical ? "FilmstripVerticalPanel" : "FilmstripHorizontalPanel"];
        ScrollViewer.SetHorizontalScrollMode(PreviewFilmstrip, vertical ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollMode(PreviewFilmstrip, vertical ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(PreviewFilmstrip, vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Visible);
        ScrollViewer.SetVerticalScrollBarVisibility(PreviewFilmstrip, vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_windowClosing || _filmstripVertical != vertical || PreviewOverlay.Visibility != Visibility.Visible ||
                _previewIndex < 0 || _previewIndex >= _currentView.Count) return;
            PreviewFilmstrip.UpdateLayout();
            PreviewFilmstrip.ScrollIntoView(_currentView[_previewIndex]);
        });
    }

    private void PreviewToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewCommands == null || PreviewCloseButton == null) return;
        var compact = e.NewSize.Width < 720;
        Grid.SetRow(PreviewCommands, compact ? 1 : 0);
        Grid.SetColumn(PreviewCommands, compact ? 0 : 1);
        Grid.SetColumnSpan(PreviewCommands, compact ? 3 : 1);
        PreviewSlideshowBtn.Visibility = PreviewFilmstripToggleBtn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PreviewZoomInButton.Visibility = PreviewZoomOutButton.Visibility = e.NewSize.Width < 400 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PreviewMore_Opening(object sender, object e)
    {
        PreviewSlideshowMenu.Text = _slideshowActive ? "暂停幻灯片" : "播放幻灯片";
        PreviewFilmstripMenu.Text = PreviewFilmstripToggleBtn.Content as string ?? "切换胶片条";
        // The menu duplicates these actions only when they don't fit in the toolbar.
        PreviewSlideshowMenu.Visibility = PreviewSlideshowBtn.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        PreviewFilmstripMenu.Visibility = PreviewFilmstripToggleBtn.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        PreviewZoomInMenu.Visibility = PreviewZoomOutMenu.Visibility = PreviewZoomInButton.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PreviewShortcuts_Click(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (PreviewOverlay.Visibility == Visibility.Visible && !_previewFullscreen)
            ((Flyout)PreviewToolbar.Resources["PreviewShortcutFlyout"]).ShowAt(PreviewMoreButton);
    });

    private void ApplyResponsivePreviewDetail()
    {
        var show = !_previewFullscreen && _previewInfoVisible;
        var overlay = _settings.InterfaceStyle == 1 && PreviewContentRoot.ActualWidth > 0 && PreviewContentRoot.ActualWidth < 820;
        DetailPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(DetailPanel, overlay ? 2 : 4);
        Canvas.SetZIndex(DetailPanel, overlay ? 1 : 0);
        DetailPanel.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        var navigationWidth = Math.Max(56, PreviewPrevBtn.DesiredSize.Width) + Math.Max(56, PreviewNextBtn.DesiredSize.Width);
        var detailSpace = PreviewContentRoot.ActualWidth - PreviewFilmstripColumn.Width.Value - navigationWidth
            - DetailPanel.Margin.Left - DetailPanel.Margin.Right;
        DetailPanel.Width = overlay ? Math.Clamp(detailSpace, 0, 310) : double.NaN;
        DetailColumn.Width = new GridLength(show && !overlay ? 310 : 0);
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
        foreach (var photo in removed) _favoriteCounter.Removed(photo);
        _allPhotos.RemoveAll(p => paths.Contains(p.FilePath));
        foreach (var photo in removed) { _selectedPaths.Remove(photo.FilePath); photo.IsSelected = false; }
        RegisterUndo($"移除 {removed.Length} 张照片", () =>
        {
            var existing = _allPhotos.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var photo in removed)
                if (existing.Add(photo.FilePath)) { _allPhotos.Add(photo); _favoriteCounter.Added(photo); }
            RebuildCategories();
        });
        RebuildCategories();
    }
}
