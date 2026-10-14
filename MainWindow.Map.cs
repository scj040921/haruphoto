using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

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
    private int _mapMotionRequest;
    private CancellationTokenSource? _mapMotionCts;
    private double _mapRenderedLatitude = 35, _mapRenderedLongitude = 104;
    private int _mapRenderedZoom = 4;
    private readonly TranslateTransform _mapDragTiles = new(), _mapDragMarkers = new();
    private sealed class MapTileView
    {
        public required Image Image { get; init; }
        public bool Ready { get; set; }
    }
    private readonly Dictionary<(int Zoom, int X, int Y), MapTileView> _mapTileViews = new();
    private readonly Dictionary<string, (Button Button, string Signature)> _mapMarkerViews = new();
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
            MapViewport.PointerWheelChanged += (_, e) =>
            {
                for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) if (ReferenceEquals(node, MapPlacementPreview)) return;
                var point = e.GetCurrentPoint(MapViewport); if (point.Properties.IsHorizontalMouseWheel) return;
                ChangeMapZoom(point.Properties.MouseWheelDelta > 0 ? 1 : -1, point.Position); e.Handled = true;
            };
            Closed += (_, _) => { _mapRenderCts?.Cancel(); _mapThumbnailLifetime.Cancel(); CancelMapMotion(); };
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
        _mapRenderedLatitude = _mapLatitude; _mapRenderedLongitude = _mapLongitude; _mapRenderedZoom = _mapZoom;
        ResetMapLayerMotion();
        MapNetworkStatus.Text = "在线底图；照片、地点和批注只保存在本机";
        MapTiles.RenderTransform = MapMarkers.RenderTransform = null;
        _mapMarkerPhotos.Clear();
        var center = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom);
        var left = center.X - MapViewport.ActualWidth / 2;
        var top = center.Y - MapViewport.ActualHeight / 2;
        var count = 1 << _mapZoom;
        var loads = new List<Task>();
        var desired = new HashSet<(int Zoom, int X, int Y)>();
        // 保留上一帧瓦片作为过渡底图；同级拖动复用 Image 和已解码的 Source。
        foreach (var entry in _mapTileViews)
        {
            var scale = Math.Pow(2, _mapZoom - entry.Key.Zoom);
            var size = 256 * scale;
            var image = entry.Value.Image;
            image.Width = image.Height = size;
            Canvas.SetLeft(image, MapGeometry.WrappedDelta((entry.Key.X + .5) * size, center.X, _mapZoom) + MapViewport.ActualWidth / 2 - size / 2);
            Canvas.SetTop(image, entry.Key.Y * size - top);
            Canvas.SetZIndex(image, 0);
        }
        for (var row = (int)Math.Floor(top / 256); row <= (int)Math.Floor((top + MapViewport.ActualHeight) / 256); row++)
        for (var col = (int)Math.Floor(left / 256); col <= (int)Math.Floor((left + MapViewport.ActualWidth) / 256); col++)
        {
            if (row < 0 || row >= count) continue;
            var key = (_mapZoom, col, row); desired.Add(key);
            if (!_mapTileViews.TryGetValue(key, out var tile))
            {
                tile = new MapTileView { Image = new Image { Width = 256, Height = 256, Stretch = Stretch.Fill } };
                _mapTileViews[key] = tile; MapTiles.Children.Add(tile.Image);
            }
            var image = tile.Image; image.Width = image.Height = 256;
            Canvas.SetLeft(image, col * 256 - left); Canvas.SetTop(image, row * 256 - top); Canvas.SetZIndex(image, 1);
            // QA 用本地空底图，不让自动测试向公益服务发起批量地图请求。
#if PERFORMANCE_TEST
            if (AppPaths.IsTestProfile) { tile.Ready = true; continue; }
#endif
            if (!tile.Ready) loads.Add(LoadMapTileAsync(tile, _mapZoom, (col % count + count) % count, row, token));
        }
        var groups = _allPhotos.Where(p => MapGeometry.Valid(p.MapLatitude, p.MapLongitude))
            .GroupBy(p => string.IsNullOrEmpty(p.MapPlaceId) ? $"{p.MapLatitude}:{p.MapLongitude}" : p.MapPlaceId).ToArray();
        var visibleMarkers = new HashSet<string>();
        foreach (var group in groups)
        {
            var photos = group.ToList(); var first = photos[0];
            var projected = MapGeometry.Project(first.MapLatitude!.Value, first.MapLongitude!.Value, _mapZoom);
            var x = MapGeometry.WrappedDelta(projected.X, center.X, _mapZoom) + MapViewport.ActualWidth / 2;
            var y = projected.Y - center.Y + MapViewport.ActualHeight / 2;
            if (x < -70 || y < -100 || x > MapViewport.ActualWidth + 70 || y > MapViewport.ActualHeight + 100) continue;
            visibleMarkers.Add(group.Key);
            foreach (var photo in photos.Take(3)) if (_mapMarkerPhotos.Count < 96) _mapMarkerPhotos.Add(photo);
            var signature = $"{photos.Count}|{first.MapLabel}|{first.MapNote}|{string.Join('|', photos.Take(3).Select(p => p.FilePath))}";
            if (_mapMarkerViews.TryGetValue(group.Key, out var existing) && existing.Signature == signature)
            {
                Canvas.SetLeft(existing.Button, x - 46); Canvas.SetTop(existing.Button, y - 75);
                foreach (var photo in photos.Take(3).Where(_mapMarkerPhotos.Contains)) _ = EnsureFilmstripThumbnailAsync(photo, _mapThumbnailLifetime.Token);
                continue;
            }
            if (existing.Button != null) MapMarkers.Children.Remove(existing.Button);
            var stack = new Grid { Width = 84, Height = 86 };
            for (var i = Math.Min(3, photos.Count) - 1; i >= 0; i--)
            {
                var photo = photos[i];
                var thumb = new Image { Width = 64, Height = 52, Stretch = Stretch.UniformToFill };
                thumb.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding { Source = photo, Path = new PropertyPath(nameof(PhotoItem.ThumbnailSource)) });
                var card = new Border { Width = 68, Height = 56, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 73, 85)), Child = thumb, VerticalAlignment = VerticalAlignment.Top,
                    RenderTransformOrigin = new Point(.5, .5), RenderTransform = new CompositeTransform { Rotation = i * 8, TranslateX = i * 4, TranslateY = i * 3 } };
                stack.Children.Add(card);
                if (_mapMarkerPhotos.Contains(photo)) _ = EnsureFilmstripThumbnailAsync(photo, _mapThumbnailLifetime.Token);
            }
            stack.Children.Add(new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(first.MapLabel) ? "地点" : first.MapLabel)} · {photos.Count}", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = stack, Padding = new Thickness(4), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 25, 32, 42)), Tag = photos };
            button.Click += (_, _) => { if (_mapPlacement.Count > 0) _ = AssignToExistingPlaceAsync(photos); else AnimateToMapPlace(photos); };
            ToolTipService.SetToolTip(button, $"{first.MapLabel} · {photos.Count} 张\n{first.MapNote}");
            Canvas.SetLeft(button, x - 46); Canvas.SetTop(button, y - 75); MapMarkers.Children.Add(button);
            _mapMarkerViews[group.Key] = (button, signature);
        }
        foreach (var key in _mapMarkerViews.Keys.Where(k => !visibleMarkers.Contains(k)).ToArray()) { MapMarkers.Children.Remove(_mapMarkerViews[key].Button); _mapMarkerViews.Remove(key); }
        if (_mapPlacement.Count == 0) MapStatus.Text = $"{groups.Length} 个地点 · 拖动地图 / 滚轮缩放 · 点击堆叠照片欣赏";
        var stale = _mapTileViews.Keys.Where(k => !desired.Contains(k)).OrderBy(k => _mapTileViews[k].Ready).ToArray();
        foreach (var key in stale.Take(Math.Max(0, _mapTileViews.Count - Math.Max(80, desired.Count * 2))))
        { MapTiles.Children.Remove(_mapTileViews[key].Image); _mapTileViews.Remove(key); }
        try { await Task.WhenAll(loads); }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested && !_windowClosing) MapNetworkStatus.Text = "底图暂不可用；本地地点仍可查看，请检查网络"; }
        if (token.IsCancellationRequested) return;
        // 只有当前瓦片就绪才撤掉旧级别底图；断网时仍保留已有底图，且有界保留。
        var ready = desired.All(k => _mapTileViews.TryGetValue(k, out var tile) && tile.Ready);
        var old = _mapTileViews.Keys.Where(k => !desired.Contains(k)).ToArray();
        foreach (var key in old.Take(ready ? old.Length : Math.Max(0, _mapTileViews.Count - Math.Max(80, desired.Count * 2))))
        { MapTiles.Children.Remove(_mapTileViews[key].Image); _mapTileViews.Remove(key); }
    }

    private async Task LoadMapTileAsync(MapTileView tile, int zoom, int x, int y, CancellationToken token)
    {
        try
        {
            var path = await _mapTiles.GetAsync(zoom, x, y, token);
            if (token.IsCancellationRequested || _windowClosing) return;
            if (path == null) { MapNetworkStatus.Text = "底图暂不可用；本地地点仍可查看，请检查网络"; return; }
            var bitmap = new BitmapImage();
            var opened = new TaskCompletionSource<bool>();
            bitmap.ImageOpened += (_, _) => { if (ReferenceEquals(tile.Image.Source, bitmap)) tile.Ready = true; opened.TrySetResult(true); };
            bitmap.ImageFailed += (_, _) => opened.TrySetResult(false);
            tile.Image.Source = bitmap; bitmap.UriSource = new Uri(path);
            if (!await opened.Task.WaitAsync(token) && !token.IsCancellationRequested) MapNetworkStatus.Text = "底图读取失败；保留已有底图";
        }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested && !_windowClosing) MapNetworkStatus.Text = "底图读取失败；地点仍保存在本机"; }
    }

    private void MapPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mapAnimating || _mapDialogOpen || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) if (node is Button or HyperlinkButton || ReferenceEquals(node, MapPlacementPreview)) return;
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
        _mapDragTiles.X = _mapDragMarkers.X = dx; _mapDragTiles.Y = _mapDragMarkers.Y = dy;
        MapTiles.RenderTransform = _mapDragTiles; MapMarkers.RenderTransform = _mapDragMarkers;
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
        if (_mapPointer != null || _mapDialogOpen) return;
        var zoom = Math.Clamp(_mapZoom + delta, 2, 17); if (zoom == _mapZoom) return;
        var point = anchor ?? new Point(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2);
        var dx = point.X - MapViewport.ActualWidth / 2; var dy = point.Y - MapViewport.ActualHeight / 2;
        var center = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapZoom);
        var scale = Math.Pow(2, zoom - _mapZoom);
        var location = MapGeometry.Unproject((center.X + dx) * scale - dx, (center.Y + dy) * scale - dy, zoom);
        _mapLatitude = location.Latitude; _mapLongitude = location.Longitude; _mapZoom = zoom; _ = AnimateMapMotionAsync(140);
    }
    private void MapZoomIn_Click(object sender, RoutedEventArgs e) => ChangeMapZoom(1);
    private void MapZoomOut_Click(object sender, RoutedEventArgs e) => ChangeMapZoom(-1);

    private void MapClose_Click(object sender, RoutedEventArgs e)
    {
        CancelMapMotion(); EndMapDrag(); _mapRenderCts?.Cancel();
        _mapThumbnailLifetime.Cancel(); _mapMarkerPhotos.Clear(); _mapPhotoContainers.Clear();
        MapOverlay.Visibility = MapPlaceOverlay.Visibility = Visibility.Collapsed; MapPlacePhotos.ItemsSource = null; _mapPlace.Clear();
        MapCancelPlacement_Click(sender, e);
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First(n => n.Tag?.ToString() == "All");
        LoadThumbnailsForCurrentPage();
        TrimThumbnailMemory();
    }

    private async Task<List<PhotoItem>> ChooseMapPhotosAsync()
    {
        using var previewLifetime = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        var list = new ListView { ItemsSource = _allPhotos, SelectionMode = ListViewSelectionMode.Multiple, Height = 360,
            ItemTemplate = (DataTemplate)((FrameworkElement)Content).Resources["MapPickerPhotoTemplate"] };
        var preview = new Image { Name = "MapPickerPreviewImage", Stretch = Stretch.Uniform, Height = 260 };
        var previewName = new TextBlock { Text = "选择照片查看预览", TextWrapping = TextWrapping.Wrap };
        var previewPanel = new StackPanel { Spacing = 10, Padding = new Thickness(12) };
        previewPanel.Children.Add(preview); previewPanel.Children.Add(previewName);
        var previews = new Grid { ColumnSpacing = 12 };
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        previews.Children.Add(list); Grid.SetColumn(previewPanel, 1); previews.Children.Add(previewPanel);
        var containers = new Dictionary<DependencyObject, PhotoItem>();
        foreach (var photo in _allPhotos.Take(8)) _ = EnsureFilmstripThumbnailAsync(photo, previewLifetime.Token);
        list.ContainerContentChanging += (sender, args) =>
        {
            if (args.InRecycleQueue) { if (containers.Remove(args.ItemContainer, out var old)) _mapPickerPhotos.Remove(old); return; }
            if (args.Item is not PhotoItem photo) return;
            containers[args.ItemContainer] = photo; _mapPickerPhotos.Add(photo);
            _ = EnsureFilmstripThumbnailAsync(photo, previewLifetime.Token);
        };
        var selected = _allPhotos.Where(p => _selectedPaths.Contains(p.FilePath)).ToHashSet();
        foreach (var photo in selected) list.SelectedItems.Add(photo);
        var search = new TextBox { PlaceholderText = "搜索文件名或文件夹…" };
        var count = new TextBlock { Text = $"已选 {selected.Count} 张" };
        var changing = false;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "对照照片预览进行多选，再去地图定位；同一照片的新位置将替代旧位置。", TextWrapping = TextWrapping.Wrap }); panel.Children.Add(search); panel.Children.Add(count); panel.Children.Add(previews);
        var dialog = new ContentDialog { Title = "选择地图照片", Content = panel, PrimaryButtonText = "下一步", CloseButtonText = "取消", XamlRoot = Content.XamlRoot, IsPrimaryButtonEnabled = list.SelectedItems.Count > 0 };
        dialog.Resources["ContentDialogMaxWidth"] = 920d;
        list.SelectionChanged += (_, e) => { if (changing) return; foreach (PhotoItem photo in e.AddedItems) selected.Add(photo); foreach (PhotoItem photo in e.RemovedItems) selected.Remove(photo); count.Text = $"已选 {selected.Count} 张"; dialog.IsPrimaryButtonEnabled = selected.Count > 0;
            var focus = e.AddedItems.OfType<PhotoItem>().LastOrDefault() ?? selected.LastOrDefault();
            _ = LoadMapPickerPreviewAsync(focus, preview, previewName, previewLifetime.Token); };
        _ = LoadMapPickerPreviewAsync(selected.LastOrDefault(), preview, previewName, previewLifetime.Token);
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
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary ? selected.ToList() : new(); }
        finally { previewLifetime.Cancel(); preview.Tag = null; preview.Source = null; _mapPickerPhotos.Clear(); TrimThumbnailMemory(); }
    }

    private async void MapChoosePhotos_Click(object sender, RoutedEventArgs e)
    {
        if (_mapDialogOpen || _mapAnimating) return;
        _mapDialogOpen = true;
        try { var selected = await ChooseMapPhotosAsync(); if (selected.Count == 0) return; BeginMapPlacement(selected); }
        catch (Exception ex) { MapStatus.Text = "无法选择照片：" + ex.Message; }
        finally { _mapDialogOpen = false; }
    }
    private void MapCancelPlacement_Click(object sender, RoutedEventArgs e) { _mapPlacement.Clear(); ClearMapPlacementPreview(); MapCancelPlacementBtn.Visibility = Visibility.Collapsed; MapStatus.Text = "拖动地图 / 滚轮缩放 · 点击堆叠照片欣赏"; }

    private void BeginMapPlacement(List<PhotoItem> photos)
    {
        _mapPlacement = photos; MapPlaceOverlay.Visibility = Visibility.Collapsed;
        MapCancelPlacementBtn.Visibility = Visibility.Visible; ShowMapPlacementPreview(0);
        MapStatus.Text = $"准备放置 {photos.Count} 张：对照左侧照片，点击地图位置或已有地点";
    }

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
        var first = photos[0]; _mapZoom = Math.Min(17, _mapZoom + 1);
        _mapLatitude = first.MapLatitude!.Value; _mapLongitude = first.MapLongitude!.Value;
        _ = FlyToMapPlaceAsync(photos);
    }

    private async Task FlyToMapPlaceAsync(List<PhotoItem> photos)
    {
        if (await AnimateMapMotionAsync(Math.Clamp(_settings.AnimationDurationMs, 50, 600))) ShowMapPlace(photos);
    }

    private async Task<bool> AnimateMapMotionAsync(double milliseconds)
    {
        _mapMotionCts?.Cancel(); _mapMotionCts?.Dispose();
        _mapMotionCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        var token = _mapMotionCts.Token; var request = ++_mapMotionRequest;
        _mapRenderCts?.Cancel(); _mapAnimating = true;
        try
        {
            if (_settings.AnimationsEnabled)
            {
                var center = MapGeometry.Project(_mapRenderedLatitude, _mapRenderedLongitude, _mapRenderedZoom);
                var target = MapGeometry.Project(_mapLatitude, _mapLongitude, _mapRenderedZoom);
                var scale = (float)Math.Pow(2, _mapZoom - _mapRenderedZoom);
                var dx = MapGeometry.WrappedDelta(target.X, center.X, _mapRenderedZoom);
                var dy = target.Y - center.Y;
                var compositor = ElementCompositionPreview.GetElementVisual(MapTiles).Compositor;
                using var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
                var completed = new TaskCompletionSource<bool>(); batch.Completed += (_, _) => completed.TrySetResult(true);
                foreach (var layer in new UIElement[] { MapTiles, MapMarkers })
                {
                    ElementCompositionPreview.SetIsTranslationEnabled(layer, true);
                    var visual = ElementCompositionPreview.GetElementVisual(layer);
                    visual.CenterPoint = new Vector3((float)MapViewport.ActualWidth / 2, (float)MapViewport.ActualHeight / 2, 0);
                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .7f), new Vector2(.2f, 1));
                    var zoom = compositor.CreateVector3KeyFrameAnimation(); zoom.Duration = TimeSpan.FromMilliseconds(milliseconds);
                    zoom.InsertExpressionKeyFrame(0, "this.StartingValue"); zoom.InsertKeyFrame(1, new Vector3(scale, scale, 1), easing);
                    var pan = compositor.CreateVector3KeyFrameAnimation(); pan.Duration = zoom.Duration;
                    pan.InsertExpressionKeyFrame(0, "this.StartingValue"); pan.InsertKeyFrame(1, new Vector3((float)(-dx * scale), (float)(-dy * scale), 0), easing);
                    visual.StartAnimation("Scale", zoom); visual.Properties.StartAnimation("Translation", pan);
                }
                batch.End(); await completed.Task.WaitAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (request != _mapMotionRequest || MapOverlay.Visibility != Visibility.Visible) return false;
            _mapAnimating = false; _ = RenderMapAsync(); return !token.IsCancellationRequested;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { if (!_windowClosing) MapStatus.Text = "地图转场未完成：" + ex.Message; return false; }
        finally { if (request == _mapMotionRequest) _mapAnimating = false; }
    }

    private void ResetMapLayerMotion()
    {
        foreach (var layer in new UIElement[] { MapTiles, MapMarkers })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(layer, true);
            var visual = ElementCompositionPreview.GetElementVisual(layer);
            visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Properties.StopAnimation("Translation");
        }
    }
    private void CancelMapMotion() { ++_mapMotionRequest; _mapMotionCts?.Cancel(); _mapAnimating = false; ResetMapLayerMotion(); }

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

    private void RefreshMapAfterUndo()
    {
        if (MapOverlay.Visibility != Visibility.Visible) return;
        if (MapPlaceOverlay.Visibility == Visibility.Visible && _mapPlace.Count > 0)
        {
            var id = _mapPlace[0].MapPlaceId;
            var photos = _allPhotos.Where(p => p.MapPlaceId == id && MapGeometry.Valid(p.MapLatitude, p.MapLongitude)).ToList();
            if (photos.Count > 0) ShowMapPlace(photos); else MapPlaceClose_Click(this, new RoutedEventArgs());
        }
        _ = RenderMapAsync();
    }

    private void MapPhoto_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) { _mapPhotoContainers.Remove(args.ItemContainer); return; }
        if (args.Item is not PhotoItem photo || _mapThumbnailLifetime.IsCancellationRequested) return;
        _mapPhotoContainers[args.ItemContainer] = photo;
        _ = EnsureFilmstripThumbnailAsync(photo, _mapThumbnailLifetime.Token);
        // 卡片展开也由合成线程驱动，避免数十张照片各自触发 UI 逐帧动画。
        ElementCompositionPreview.SetIsTranslationEnabled(args.ItemContainer, true);
        var visual = ElementCompositionPreview.GetElementVisual(args.ItemContainer);
        visual.StopAnimation("Scale"); visual.Properties.InsertVector3("Translation", Vector3.Zero); visual.Properties.StopAnimation("Translation");
        visual.Scale = Vector3.One;
        if (!_settings.AnimationsEnabled) return;
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)Math.Max(180, args.ItemContainer.ActualWidth) / 2, 70, 0);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .7f), new Vector2(.2f, 1));
        var scale = compositor.CreateVector3KeyFrameAnimation(); scale.Duration = TimeSpan.FromMilliseconds(280); scale.DelayTime = TimeSpan.FromMilliseconds(args.ItemIndex % 6 * 20);
        scale.InsertKeyFrame(0, new Vector3(.9f, .9f, 1)); scale.InsertKeyFrame(1, Vector3.One, easing);
        var offset = compositor.CreateVector3KeyFrameAnimation(); offset.Duration = scale.Duration; offset.DelayTime = scale.DelayTime;
        offset.InsertKeyFrame(0, new Vector3(0, 20, 0)); offset.InsertKeyFrame(1, Vector3.Zero, easing);
        visual.StartAnimation("Scale", scale); visual.Properties.StartAnimation("Translation", offset);
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
