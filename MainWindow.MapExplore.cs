using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    public sealed record MapPlaceEntry(List<PhotoItem> Photos)
    {
        public PhotoItem Photo => Photos[0];
        public string Title => string.IsNullOrWhiteSpace(Photo.MapLabel) ? "未命名地点" : Photo.MapLabel;
        public string Subtitle => $"{Photos.Count} 张照片" + (string.IsNullOrWhiteSpace(Photo.MapNote) ? "" : $" · {Photo.MapNote}");
    }

    private List<MapPlaceEntry> SavedMapPlaces() => _allPhotos
        .Where(p => MapGeometry.Valid(p.MapLatitude, p.MapLongitude))
        .GroupBy(p => string.IsNullOrEmpty(p.MapPlaceId) ? $"{p.MapLatitude}:{p.MapLongitude}" : p.MapPlaceId)
        .Select(g => new MapPlaceEntry(g.ToList()))
        .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(p => p.Photo.MapLatitude).ThenBy(p => p.Photo.MapLongitude).ToList();

    private void MapPlacesFlyout_Opening(object sender, object e) => RefreshMapPlacesList();
    private void MapPlacesFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshMapPlacesList();
    private void RefreshMapPlacesList()
    {
        if (MapPlacesList == null) return;
        var term = MapPlacesFilter.Text.Trim();
        var places = SavedMapPlaces().Where(p => p.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (p.Photo.MapNote ?? "").Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToList();
        MapPlacesList.ItemsSource = places;
        MapPlacesEmpty.Text = term.Length == 0 ? "还没有地点。选择照片，在地图上留下第一段记忆。" : "没有匹配的地点，试试其他名称或批注。";
        MapPlacesEmpty.Visibility = places.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var place in places.Take(16)) _ = EnsureFilmstripThumbnailAsync(place.Photo, _mapThumbnailLifetime.Token);
    }

    private void MapPlacesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MapPlaceEntry place) return;
        MapPlacesFlyout.Hide();
        if (_mapPlacement.Count > 0) _ = AssignToExistingPlaceAsync(place.Photos);
        else AnimateToMapPlace(place.Photos);
    }

    private void UpdateMapPlaceNavigation()
    {
        var places = SavedMapPlaces();
        var index = places.FindIndex(p => _mapPlace.Contains(p.Photo));
        MapPreviousPlaceButton.IsEnabled = index > 0;
        MapNextPlaceButton.IsEnabled = index >= 0 && index < places.Count - 1;
        MapPlacePositionText.Text = index >= 0 ? $"{index + 1} / {places.Count} 个地点" : "";
    }

    private void MoveMapPlace(int direction)
    {
        if (_mapPhotoPreviewReturnRunning || PreviewOverlay.Visibility == Visibility.Visible) return;
        var places = SavedMapPlaces();
        var index = places.FindIndex(p => _mapPlace.Contains(p.Photo));
        var next = index + direction;
        if (index < 0 || next < 0 || next >= places.Count) return;
        AnimateToMapPlace(places[next].Photos);
    }
    private bool _mapPlaceReelHidden;
    private int _mapReelRequest;
    private PhotoItem? _mapPlaceLastPhoto;

    private void RestoreMapPlacePhotoPosition()
    {
        var photo = _mapPlaceLastPhoto != null && _mapPlace.Contains(_mapPlaceLastPhoto)
            ? _mapPlaceLastPhoto : _mapPlace.FirstOrDefault();
        MapPlacePhotos.SelectedItem = photo;
        if (photo != null && !_mapPlaceReelHidden)
        {
            MapPlacePhotos.UpdateLayout();
            MapPlacePhotos.ScrollIntoView(photo, ScrollIntoViewAlignment.Leading);
        }
    }

    private void ApplyMapPlaceReelVisibility()
    {
        ++_mapReelRequest;
        ResetMapVisual(MapPlaceReel);
        MapPlaceReel.Visibility = _mapPlaceReelHidden ? Visibility.Collapsed : Visibility.Visible;
        MapReelToggleItem.Text = _mapPlaceReelHidden ? "展开照片条（H）" : "收起照片条（H）";
    }

    private async Task ToggleMapPlaceReelAsync()
    {
        if (MapPlaceOverlay.Visibility != Visibility.Visible || _mapPlaceClosing) return;
        CancelMapPlaceTransition();
        _mapPlaceReelHidden = !_mapPlaceReelHidden;
        var request = ++_mapReelRequest;
        MapReelToggleItem.Text = _mapPlaceReelHidden ? "展开照片条（H）" : "收起照片条（H）";
        ResetMapVisual(MapPlaceReel);
        try
        {
            if (!_mapPlaceReelHidden)
            {
                MapPlaceReel.Visibility = Visibility.Visible;
                RestoreMapPlacePhotoPosition();
                if (MotionEnabled) MapFade(MapVisual(MapPlaceReel), .35f, 1, 140);
            }
            else
            {
                if (MotionEnabled)
                    await MapAnimationBatchAsync(() => MapFade(MapVisual(MapPlaceReel), 1, 0, 140), _windowLifetime.Token);
                if (request == _mapReelRequest) MapPlaceReel.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task LocateCurrentMapPlaceAsync()
    {
        if (_mapPlaceClosing || _mapPlace.Count == 0 || MapPlaceOverlay.Visibility != Visibility.Visible) return;
        var photo = _mapPlace[0];
        if (!MapGeometry.Valid(photo.MapLatitude, photo.MapLongitude)) return;
        _mapLatitude = photo.MapLatitude!.Value; _mapLongitude = photo.MapLongitude!.Value; _mapZoom = 17;
        await AnimateMapMotionAsync(MapTransitionDuration, true);
    }
    private async void MapLocatePlace_Click(object sender, RoutedEventArgs e) => await LocateCurrentMapPlaceAsync();
    private async void MapToggleReel_Click(object sender, RoutedEventArgs e) => await ToggleMapPlaceReelAsync();

    private bool TryHandleMapSceneKey(Windows.System.VirtualKey key, bool ctrl)
    {
        if (MapOverlay.Visibility != Visibility.Visible || PreviewOverlay.Visibility == Visibility.Visible) return false;
        if (key == Windows.System.VirtualKey.Escape)
        {
            if (_mapPlacement.Count > 0) MapCancelPlacement_Click(this, new RoutedEventArgs());
            else if (MapPlaceOverlay.Visibility == Visibility.Visible) MapPlaceClose_Click(this, new RoutedEventArgs());
            else MapClose_Click(this, new RoutedEventArgs());
            return true;
        }
        if (MapPlaceOverlay.Visibility != Visibility.Visible) return false;
        if (key is Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown)
            MoveMapPlace(key == Windows.System.VirtualKey.PageUp ? -1 : 1);
        else if (key == Windows.System.VirtualKey.Home) _ = LocateCurrentMapPlaceAsync();
        else if (!ctrl && key == Windows.System.VirtualKey.H) _ = ToggleMapPlaceReelAsync();
        else return false;
        return true;
    }

    private void MapPreviousPlace_Click(object sender, RoutedEventArgs e) => MoveMapPlace(-1);
    private void MapNextPlace_Click(object sender, RoutedEventArgs e) => MoveMapPlace(1);
}
