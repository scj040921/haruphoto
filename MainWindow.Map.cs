using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Microsoft.UI.Input;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private readonly MapTileCache _mapTiles = new();
    private CancellationTokenSource? _mapRenderCts;
    private double _mapLatitude = 35, _mapLongitude = 104;
    private int _mapZoom = 4;
    private bool _mapInitialized, _mapDialogOpen, _mapAnimating;
    private uint? _mapPointer;
    private Point _mapPointerStart;
    private (double X, double Y) _mapDragCenter;
    private bool _mapDragged;
    private List<PhotoItem> _mapPlacement = new(), _mapPlace = new();
    private List<PhotoItem>? _mapPreviewRestore;
    private DispatcherTimer? _mapAnimationTimer;
    private CancellationTokenSource _mapThumbnailLifetime = new();
    private readonly HashSet<PhotoItem> _mapMarkerPhotos = new();
    private readonly Dictionary<DependencyObject, PhotoItem> _mapPhotoContainers = new();

    private async void OpenMap()
    {
        _thumbCts?.Cancel();
        if (_mapThumbnailLifetime.IsCancellationRequested) { _mapThumbnailLifetime.Dispose(); _mapThumbnailLifetime = new(); }
        MapOverlay.Visibility = Visibility.Visible;
        if (!_mapInitialized)
        {
            _mapInitialized = true;
            var first = _allPhotos.FirstOrDefault(p => MapGeometry.Valid(p.MapLatitude, p.MapLongitude));
            if (first != null) { _mapLatitude = first.MapLatitude!.Value; _mapLongitude = first.MapLongitude!.Value; _mapZoom = 7; }
            MapViewport.PointerPressed += MapPointerPressed;
            MapViewport.PointerMoved += MapPointerMoved;
            MapViewport.PointerReleased += MapPointerReleased;
            MapViewport.PointerCaptureLost += (_, _) => { if (_mapPointer != null) { EndMapDrag(); _ = RenderMapAsync(); } };
            Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && _mapPointer != null) { EndMapDrag(); _ = RenderMapAsync(); } };
            MapViewport.PointerWheelChanged += (_, e) => { if (_mapAnimating) return; ChangeMapZoom(e.GetCurrentPoint(MapViewport).Properties.MouseWheelDelta > 0 ? 1 : -1, e.GetCurrentPoint(MapViewport).Position); e.Handled = true; };
            Closed += (_, _) => { _mapRenderCts?.Cancel(); _mapThumbnailLifetime.Cancel(); _mapAnimationTimer?.Stop(); };
            _ = Task.Run(MapTileCache.PruneExpired);
        }
        try { await RenderMapAsync(); } catch (Exception ex) { if (!_windowClosing) MapStatus.Text = "地图加载未完成：" + ex.Message; }
    }

    private void MapViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MapViewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height)) };
        if (MapOverlay.Visibility == Visibility.Visible && !_mapAnimating) _ = RenderMapAsync();
    }

    private async Task RenderMapAsync()
    {
        if (_windowClosing || MapOverlay.Visibility != Visibility.Visible || MapViewport.ActualWidth <= 0) return;
        _mapRenderCts?.Cancel(); _mapRenderCts?.Dispose();
        _mapRenderCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        var token = _mapRenderCts.Token;
        MapNetworkStatus.Text = "在线底图；照片、地点和批注只保存在本机";
        MapTiles.RenderTransform = MapMarkers.RenderTransform = null;
        MapTiles.Children.Clear(); MapMarkers.Children.Clear(); _mapMarkerPhotos.Clear();
        var center = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom);
        var left = center.X - MapViewport.ActualWidth / 2;
        var top = center.Y - MapViewport.ActualHeight / 2;
        var count = 1 << _mapZoom;
        var loads = new List<Task>();
        for (var row = (int)Math.Floor(top / 256); row <= (int)Math.Floor((top + MapViewport.ActualHeight) / 256); row++)
        for (var col = (int)Math.Floor(left / 256); col <= (int)Math.Floor((left + MapViewport.ActualWidth) / 256); col++)
        {
            if (row < 0 || row >= count) continue;
            var image = new Image { Width = 256, Height = 256, Stretch = Stretch.Fill };
            Canvas.SetLeft(image, col * 256 - left); Canvas.SetTop(image, row * 256 - top); MapTiles.Children.Add(image);
            // QA 用本地空底图，不让自动测试向公益服务发起批量地图请求。
#if PERFORMANCE_TEST
            if (AppPaths.IsTestProfile) continue;
#endif
            loads.Add(LoadMapTileAsync(image, _mapZoom, (col % count + count) % count, row, token));
        }
        var groups = _allPhotos.Where(p => MapGeometry.Valid(p.MapLatitude, p.MapLongitude))
            .GroupBy(p => string.IsNullOrEmpty(p.MapPlaceId) ? $"{p.MapLatitude}:{p.MapLongitude}" : p.MapPlaceId).ToArray();
        foreach (var group in groups)
        {
            var photos = group.ToList(); var first = photos[0];
            var projected = MapGeometry.Project(first.MapLatitude!.Value, first.MapLongitude!.Value, _mapZoom);
            var x = MapGeometry.WrappedDelta(projected.X, center.X, _mapZoom) + MapViewport.ActualWidth / 2;
            var y = projected.Y - center.Y + MapViewport.ActualHeight / 2;
            if (x < -70 || y < -100 || x > MapViewport.ActualWidth + 70 || y > MapViewport.ActualHeight + 100) continue;
            var stack = new Grid { Width = 84, Height = 86 };
            for (var i = Math.Min(3, photos.Count) - 1; i >= 0; i--)
            {
                var photo = photos[i];
                var thumb = new Image { Width = 64, Height = 52, Stretch = Stretch.UniformToFill };
                thumb.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding { Source = photo, Path = new PropertyPath(nameof(PhotoItem.ThumbnailSource)) });
                var card = new Border { Width = 68, Height = 56, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 73, 85)), Child = thumb, VerticalAlignment = VerticalAlignment.Top,
                    RenderTransformOrigin = new Point(.5, .5), RenderTransform = new CompositeTransform { Rotation = i * 8, TranslateX = i * 4, TranslateY = i * 3 } };
                stack.Children.Add(card);
                if (_mapMarkerPhotos.Count < 96) { _mapMarkerPhotos.Add(photo); _ = EnsureFilmstripThumbnailAsync(photo, _mapThumbnailLifetime.Token); }
            }
            stack.Children.Add(new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(first.MapLabel) ? "地点" : first.MapLabel)} · {photos.Count}", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = stack, Padding = new Thickness(4), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 25, 32, 42)), Tag = photos };
            button.Click += (_, _) => { if (_mapPlacement.Count > 0) _ = AssignToExistingPlaceAsync(photos); else AnimateToMapPlace(photos); };
            ToolTipService.SetToolTip(button, $"{first.MapLabel} · {photos.Count} 张\n{first.MapNote}");
            Canvas.SetLeft(button, x - 46); Canvas.SetTop(button, y - 75); MapMarkers.Children.Add(button);
        }
        if (_mapPlacement.Count == 0) MapStatus.Text = $"{groups.Length} 个地点 · 拖动地图 / 滚轮缩放 · 点击堆叠照片欣赏";
        try { await Task.WhenAll(loads); }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested && !_windowClosing) MapNetworkStatus.Text = "底图暂不可用；本地地点仍可查看，请检查网络"; }
    }

    private async Task LoadMapTileAsync(Image image, int zoom, int x, int y, CancellationToken token)
    {
        try
        {
            var path = await _mapTiles.GetAsync(zoom, x, y, token);
            if (token.IsCancellationRequested || _windowClosing) return;
            if (path == null) { MapNetworkStatus.Text = "底图暂不可用；本地地点仍可查看，请检查网络"; return; }
            image.Source = new BitmapImage(new Uri(path));
        }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested && !_windowClosing) MapNetworkStatus.Text = "底图读取失败；地点仍保存在本机"; }
    }

    private void MapPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mapAnimating || _mapDialogOpen || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) if (node is Button or HyperlinkButton) return;
        var point = e.GetCurrentPoint(MapViewport); if (!point.Properties.IsLeftButtonPressed) return;
        _mapPointerStart = point.Position; _mapDragCenter = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom); _mapDragged = false;
        if (MapViewport.CapturePointer(e.Pointer)) _mapPointer = e.Pointer.PointerId;
        e.Handled = true;
    }

    private void MapPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_mapPointer != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(MapViewport);
        if (!point.Properties.IsLeftButtonPressed) { EndMapDrag(); return; }
        var dx = point.Position.X - _mapPointerStart.X; var dy = point.Position.Y - _mapPointerStart.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 5) _mapDragged = true;
        MapTiles.RenderTransform = new TranslateTransform { X = dx, Y = dy };
        MapMarkers.RenderTransform = new TranslateTransform { X = dx, Y = dy };
        e.Handled = true;
    }

    private async void MapPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_mapPointer != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(MapViewport).Position;
        var dragged = _mapDragged; EndMapDrag();
        if (dragged)
        {
            var center = MapGeometry.Unproject(_mapDragCenter.X - position.X + _mapPointerStart.X, _mapDragCenter.Y - position.Y + _mapPointerStart.Y, _mapZoom);
            _mapLatitude = center.Latitude; _mapLongitude = center.Longitude; await RenderMapAsync();
        }
        else if (_mapPlacement.Count > 0)
        {
            var center = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom);
            var location = MapGeometry.Unproject(center.X + position.X - MapViewport.ActualWidth / 2, center.Y + position.Y - MapViewport.ActualHeight / 2, _mapZoom);
            await PlacePhotosAsync(location.Latitude, location.Longitude);
        }
        e.Handled = true;
    }

    private void EndMapDrag() { _mapPointer = null; MapViewport.ReleasePointerCaptures(); MapTiles.RenderTransform = MapMarkers.RenderTransform = null; }
    private void ChangeMapZoom(int delta, Point? anchor = null)
    {
        if (_mapAnimating || _mapPointer != null) return;
        var zoom = Math.Clamp(_mapZoom + delta, 2, 17); if (zoom == _mapZoom) return;
        var point = anchor ?? new Point(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2);
        var dx = point.X - MapViewport.ActualWidth / 2; var dy = point.Y - MapViewport.ActualHeight / 2;
        var center = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom);
        var scale = Math.Pow(2, zoom - _mapZoom);
        var location = MapGeometry.Unproject((center.X + dx) * scale - dx, (center.Y + dy) * scale - dy, zoom);
        _mapLatitude = location.Latitude; _mapLongitude = location.Longitude; _mapZoom = zoom; _ = RenderMapAsync();
    }
    private void MapZoomIn_Click(object sender, RoutedEventArgs e) => ChangeMapZoom(1);
    private void MapZoomOut_Click(object sender, RoutedEventArgs e) => ChangeMapZoom(-1);

    private void MapClose_Click(object sender, RoutedEventArgs e)
    {
        _mapAnimationTimer?.Stop(); _mapAnimating = false; EndMapDrag(); _mapRenderCts?.Cancel();
        _mapThumbnailLifetime.Cancel(); _mapMarkerPhotos.Clear(); _mapPhotoContainers.Clear();
        MapOverlay.Visibility = MapPlaceOverlay.Visibility = Visibility.Collapsed; MapPlacePhotos.ItemsSource = null; _mapPlace.Clear();
        MapCancelPlacement_Click(sender, e);
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First(n => n.Tag?.ToString() == "All");
        LoadThumbnailsForCurrentPage();
        TrimThumbnailMemory();
    }

    private async Task<List<PhotoItem>> ChooseMapPhotosAsync()
    {
        var list = new ListView { ItemsSource = _allPhotos, DisplayMemberPath = nameof(PhotoItem.Filename), SelectionMode = ListViewSelectionMode.Multiple, Height = 340 };
        var selected = _allPhotos.Where(p => _selectedPaths.Contains(p.FilePath)).ToHashSet();
        foreach (var photo in selected) list.SelectedItems.Add(photo);
        var search = new TextBox { PlaceholderText = "搜索文件名或文件夹…" };
        var count = new TextBlock { Text = $"已选 {selected.Count} 张" };
        var changing = false;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "同一照片只能属于一个地点，新位置会替代旧位置。支持多选，不上传照片。", TextWrapping = TextWrapping.Wrap }); panel.Children.Add(search); panel.Children.Add(count); panel.Children.Add(list);
        var dialog = new ContentDialog { Title = "选择地图照片", Content = panel, PrimaryButtonText = "下一步", CloseButtonText = "取消", XamlRoot = Content.XamlRoot, IsPrimaryButtonEnabled = list.SelectedItems.Count > 0 };
        list.SelectionChanged += (_, e) => { if (changing) return; foreach (PhotoItem photo in e.AddedItems) selected.Add(photo); foreach (PhotoItem photo in e.RemovedItems) selected.Remove(photo); count.Text = $"已选 {selected.Count} 张"; dialog.IsPrimaryButtonEnabled = selected.Count > 0; };
        search.TextChanged += (_, _) =>
        {
            changing = true;
            try
            {
                var visible = _allPhotos.Where(p => p.Filename.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || p.FilePath.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
                list.ItemsSource = visible;
                foreach (var photo in visible.Where(selected.Contains)) list.SelectedItems.Add(photo);
            }
            finally { changing = false; }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? selected.ToList() : new();
    }

    private async void MapChoosePhotos_Click(object sender, RoutedEventArgs e)
    {
        if (_mapDialogOpen || _mapAnimating) return;
        _mapDialogOpen = true;
        try { var selected = await ChooseMapPhotosAsync(); if (selected.Count == 0) return; _mapPlacement = selected; MapPlaceOverlay.Visibility = Visibility.Collapsed; MapCancelPlacementBtn.Visibility = Visibility.Visible; MapStatus.Text = $"准备放置 {selected.Count} 张：点击地图位置或已有地点（会替换这些照片的旧位置）"; }
        catch (Exception ex) { MapStatus.Text = "无法选择照片：" + ex.Message; }
        finally { _mapDialogOpen = false; }
    }
    private void MapCancelPlacement_Click(object sender, RoutedEventArgs e) { _mapPlacement.Clear(); MapCancelPlacementBtn.Visibility = Visibility.Collapsed; MapStatus.Text = "拖动地图 / 滚轮缩放 · 点击堆叠照片欣赏"; }

    private async Task PlacePhotosAsync(double latitude, double longitude)
    {
        if (_mapDialogOpen) return;
        _mapDialogOpen = true;
        try
        {
            var label = new TextBox { Header = "地点名称", Text = "我的地点", MaxLength = 80 };
            var note = new TextBox { Header = "批注（仅保存在本机）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, MaxLength = 2000 };
            var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(new TextBlock { Text = $"{latitude:F5}, {longitude:F5} · {_mapPlacement.Count} 张照片" }); panel.Children.Add(label); panel.Children.Add(note);
            var dialog = new ContentDialog { Title = "放置照片", Content = panel, PrimaryButtonText = "放在这里", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            SaveMapPlacement(_mapPlacement, latitude, longitude, Guid.NewGuid().ToString("N"), label.Text, note.Text);
            MapCancelPlacement_Click(this, new RoutedEventArgs()); await RenderMapAsync();
        }
        catch (Exception ex) { MapStatus.Text = "未能保存地点：" + ex.Message; }
        finally { _mapDialogOpen = false; }
    }

    private void SaveMapPlacement(IEnumerable<PhotoItem> photos, double? latitude, double? longitude, string id, string label, string note)
    {
        if ((latitude != null || longitude != null) && !MapGeometry.Valid(latitude, longitude)) throw new ArgumentOutOfRangeException(nameof(latitude));
        var targets = photos.Where(_allPhotos.Contains).ToArray();
        var before = targets.Select(p => (Photo: p, p.MapLatitude, p.MapLongitude, p.MapPlaceId, p.MapLabel, p.MapNote)).ToArray();
        foreach (var photo in targets) { photo.MapLatitude = latitude; photo.MapLongitude = longitude; photo.MapPlaceId = id; photo.MapLabel = label.Trim(); photo.MapNote = note; }
        RegisterUndo("地图位置 / 批注", () => { foreach (var item in before) { item.Photo.MapLatitude = item.MapLatitude; item.Photo.MapLongitude = item.MapLongitude; item.Photo.MapPlaceId = item.MapPlaceId; item.Photo.MapLabel = item.MapLabel; item.Photo.MapNote = item.MapNote; } if (MapOverlay.Visibility == Visibility.Visible) _ = RenderMapAsync(); });
        ScheduleSave();
    }

    private async Task AssignToExistingPlaceAsync(List<PhotoItem> photos)
    {
        if (_mapDialogOpen || photos.Count == 0) return;
        _mapDialogOpen = true;
        try
        {
            var first = photos[0];
            var confirm = new ContentDialog { Title = "加入地点", Content = $"将 {_mapPlacement.Count} 张照片放入「{first.MapLabel}」？已有的位置会替换。", PrimaryButtonText = "加入", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            SaveMapPlacement(_mapPlacement, first.MapLatitude, first.MapLongitude, first.MapPlaceId, first.MapLabel, first.MapNote);
            MapCancelPlacement_Click(this, new RoutedEventArgs()); await RenderMapAsync();
        }
        catch (Exception ex) { MapStatus.Text = "未能加入地点：" + ex.Message; }
        finally { _mapDialogOpen = false; }
    }

    private void AnimateToMapPlace(List<PhotoItem> photos)
    {
        if (_mapAnimating || photos.Count == 0) return;
        var first = photos[0]; var targetZoom = Math.Min(17, _mapZoom + 1);
        var center = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom);
        var target = MapGeometry.Project(first.MapLatitude!.Value, first.MapLongitude!.Value, _mapZoom);
        var dx = MapGeometry.WrappedDelta(target.X, center.X, _mapZoom); var dy = target.Y - center.Y;
        _mapRenderCts?.Cancel(); _mapAnimating = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        _mapAnimationTimer?.Stop(); _mapAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _mapAnimationTimer.Tick += (_, _) =>
        {
            var t = Math.Min(1, started.Elapsed.TotalMilliseconds / (!_settings.AnimationsEnabled ? 1 : Math.Clamp(_settings.AnimationDurationMs, 50, 1000)));
            var eased = 1 - Math.Pow(1 - t, 3); var scale = Math.Pow(2, (targetZoom - _mapZoom) * eased);
            var transform = new MatrixTransform { Matrix = new Matrix(scale, 0, 0, scale, MapViewport.ActualWidth / 2 * (1 - scale) - dx * eased * scale, MapViewport.ActualHeight / 2 * (1 - scale) - dy * eased * scale) };
            MapTiles.RenderTransform = transform; MapMarkers.RenderTransform = transform;
            if (t < 1) return;
            _mapAnimationTimer!.Stop(); _mapAnimating = false;
            _mapLatitude = first.MapLatitude.Value; _mapLongitude = first.MapLongitude.Value; _mapZoom = targetZoom;
            _ = RenderMapAsync(); ShowMapPlace(photos);
        };
        _mapAnimationTimer.Start();
    }

    private void ShowMapPlace(List<PhotoItem> photos)
    {
        _mapPlace = photos.Where(_allPhotos.Contains).ToList(); if (_mapPlace.Count == 0) return;
        var first = _mapPlace[0]; MapPlaceTitle.Text = $"{first.MapLabel} · {_mapPlace.Count} 张"; MapPlaceNote.Text = first.MapNote;
        MapPlacePhotos.ItemsSource = _mapPlace; MapPlaceOverlay.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(!_settings.AnimationsEnabled ? 1 : 280) };
        var storyboard = new Storyboard(); Storyboard.SetTarget(animation, MapPlaceOverlay); Storyboard.SetTargetProperty(animation, "Opacity"); storyboard.Children.Add(animation); storyboard.Begin();
        foreach (var photo in _mapPlace.Take(100)) _ = EnsureFilmstripThumbnailAsync(photo, _mapThumbnailLifetime.Token);
    }
    private void MapPlaceClose_Click(object sender, RoutedEventArgs e) { MapPlaceOverlay.Visibility = Visibility.Collapsed; MapPlacePhotos.ItemsSource = null; _mapPhotoContainers.Clear(); _mapPlace.Clear(); TrimThumbnailMemory(); _ = RenderMapAsync(); }
    private void MapPhoto_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PhotoItem photo) return;
        _mapPreviewRestore = _currentView; _currentView = _mapPlace.ToList(); OpenPreview(photo);
    }
    private void RestoreMapPreviewView() { if (_mapPreviewRestore == null) return; _currentView = _mapPreviewRestore; _mapPreviewRestore = null; }

    private void MapPhoto_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) { _mapPhotoContainers.Remove(args.ItemContainer); return; }
        if (args.Item is not PhotoItem photo || _mapThumbnailLifetime.IsCancellationRequested) return;
        _mapPhotoContainers[args.ItemContainer] = photo;
        _ = EnsureFilmstripThumbnailAsync(photo, _mapThumbnailLifetime.Token);
        if (!_settings.AnimationsEnabled) return;
        // 从地点卡片展开：轻微放大与错峰淡入，而非一次性切换整页。
        var transform = new CompositeTransform { ScaleX = .85, ScaleY = .85, TranslateY = 28 };
        args.ItemContainer.RenderTransformOrigin = new Point(.5, .5); args.ItemContainer.RenderTransform = transform;
        var storyboard = new Storyboard();
        foreach (var (property, start, end) in new[] { ("ScaleX", .85, 1d), ("ScaleY", .85, 1d), ("TranslateY", 28d, 0d) })
        {
            var animation = new DoubleAnimation { From = start, To = end, BeginTime = TimeSpan.FromMilliseconds(args.ItemIndex % 6 * 25), Duration = TimeSpan.FromMilliseconds(320), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
            Storyboard.SetTarget(animation, transform); Storyboard.SetTargetProperty(animation, property); storyboard.Children.Add(animation);
        }
        storyboard.Begin();
    }

    private async void MapEditPlace_Click(object sender, RoutedEventArgs e)
    {
        if (_mapDialogOpen || _mapPlace.Count == 0) return;
        _mapDialogOpen = true;
        try
        {
            var first = _mapPlace[0];
            var label = new TextBox { Header = "地点名称", Text = first.MapLabel, MaxLength = 80 };
            var note = new TextBox { Header = "批注", Text = first.MapNote, AcceptsReturn = true, Height = 120, TextWrapping = TextWrapping.Wrap, MaxLength = 2000 };
            var lat = new NumberBox { Header = "纬度（−90 至 90）", Value = first.MapLatitude!.Value, Minimum = -90, Maximum = 90 };
            var lon = new NumberBox { Header = "经度（−180 至 180）", Value = first.MapLongitude!.Value, Minimum = -180, Maximum = 180 };
            var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(label); panel.Children.Add(note); panel.Children.Add(lat); panel.Children.Add(lon);
            var dialog = new ContentDialog { Title = "编辑地点", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !MapGeometry.Valid(lat.Value, lon.Value)) return;
            SaveMapPlacement(_mapPlace, lat.Value, lon.Value, first.MapPlaceId, label.Text, note.Text); ShowMapPlace(_mapPlace); await RenderMapAsync();
        }
        catch (Exception ex) { MapStatus.Text = "未能修改地点：" + ex.Message; }
        finally { _mapDialogOpen = false; }
    }
    private async void MapAddToPlace_Click(object sender, RoutedEventArgs e)
    {
        if (_mapDialogOpen || _mapPlace.Count == 0) return;
        _mapDialogOpen = true;
        try
        {
            var selected = await ChooseMapPhotosAsync(); if (selected.Count == 0) return;
            var first = _mapPlace[0]; SaveMapPlacement(selected, first.MapLatitude, first.MapLongitude, first.MapPlaceId, first.MapLabel, first.MapNote);
            ShowMapPlace(_allPhotos.Where(p => p.MapPlaceId == first.MapPlaceId).ToList()); await RenderMapAsync();
        }
        catch (Exception ex) { MapStatus.Text = "未能添加照片：" + ex.Message; }
        finally { _mapDialogOpen = false; }
    }
    private async void MapRemovePlace_Click(object sender, RoutedEventArgs e)
    {
        if (_mapDialogOpen || _mapPlace.Count == 0) return;
        _mapDialogOpen = true;
        try
        {
            var dialog = new ContentDialog { Title = "移除地图地点", Content = "只清除该地点照片的位置和批注。照片仍在图库，原文件不删除。", PrimaryButtonText = "移除地点", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            SaveMapPlacement(_mapPlace, null, null, "", "", ""); MapPlaceClose_Click(sender, e);
        }
        catch (Exception ex) { MapStatus.Text = "未能移除地点：" + ex.Message; }
        finally { _mapDialogOpen = false; }
    }
}
