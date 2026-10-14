using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private readonly HashSet<PhotoItem> _mapPickerPhotos = new();
    private int _mapPlacementPreviewIndex;
    private sealed class MapPreviewRequest { public BitmapImage? Bitmap { get; set; } }

    private async Task LoadMapPickerPreviewAsync(PhotoItem? photo, Image image, TextBlock caption, CancellationToken token)
    {
        var request = new MapPreviewRequest(); image.Tag = request; image.Source = null;
        caption.Text = photo?.Filename ?? "选择照片查看预览";
        if (photo == null || token.IsCancellationRequested) return;
        try
        {
            await EnsureFilmstripThumbnailAsync(photo, token);
            if (token.IsCancellationRequested || _windowClosing || !ReferenceEquals(image.Tag, request)) return;
            image.Source = photo.ThumbnailSource;
            // 预览单独限宽解码，不为列表中所有照片解码原图。
            var bitmap = photo.ThumbnailAspectRatio < .6 ? new BitmapImage { DecodePixelHeight = 1200 } : new BitmapImage { DecodePixelWidth = 1000 };
            request.Bitmap = bitmap;
            bitmap.ImageFailed += (_, _) => { if (!token.IsCancellationRequested && ReferenceEquals(image.Tag, request)) caption.Text = photo.Filename + " · 无法读取"; };
            bitmap.ImageOpened += (_, _) => { if (!token.IsCancellationRequested && ReferenceEquals(image.Tag, request)) image.Source = bitmap; };
            bitmap.UriSource = new Uri(photo.FilePath);
        }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested && ReferenceEquals(image.Tag, request)) caption.Text = photo.Filename + " · 无法读取"; }
    }

    private void ShowMapPlacementPreview(int index)
    {
        if (_mapPlacement.Count == 0) { ClearMapPlacementPreview(); return; }
        _mapPlacementPreviewIndex = (index % _mapPlacement.Count + _mapPlacement.Count) % _mapPlacement.Count;
        MapPlacementPreview.Visibility = Visibility.Visible;
        MapPlacementCount.Text = $"准备放置 {_mapPlacement.Count} 张 · 预览 {_mapPlacementPreviewIndex + 1}/{_mapPlacement.Count}";
        _ = LoadMapPickerPreviewAsync(_mapPlacement[_mapPlacementPreviewIndex], MapPlacementImage, MapPlacementName, _mapThumbnailLifetime.Token);
    }

    private void ClearMapPlacementPreview()
    {
        MapPlacementPreview.Visibility = Visibility.Collapsed;
        MapPlacementImage.Tag = null; MapPlacementImage.Source = null; _mapPlacementPreviewIndex = 0;
    }
    private void MapPlacementPrev_Click(object sender, RoutedEventArgs e) => ShowMapPlacementPreview(_mapPlacementPreviewIndex - 1);
    private void MapPlacementNext_Click(object sender, RoutedEventArgs e) => ShowMapPlacementPreview(_mapPlacementPreviewIndex + 1);
}
