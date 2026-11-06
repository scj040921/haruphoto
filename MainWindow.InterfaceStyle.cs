using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private int? _appliedInterfaceStyle;
    // The style changes presentation only. Existing theme and layout preferences stay intact.
    private void ApplyInterfaceStyle()
    {
        var exhibition = _settings.InterfaceStyle == 1;
        GalleryTitleText.FontSize = exhibition ? 23 : 19;
        GalleryTitleText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        LibraryNameText.FontSize = exhibition ? 13 : 12;
        NavView.OpenPaneLength = exhibition ? 224 : 210;
        if (_appliedInterfaceStyle != _settings.InterfaceStyle)
        {
            NavView.PaneDisplayMode = exhibition ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Auto;
            NavView.IsPaneOpen = !exhibition;
            _appliedInterfaceStyle = _settings.InterfaceStyle;
        }
        TopBarTint.Visibility = TopBarGradient.Visibility = exhibition ? Visibility.Collapsed : Visibility.Visible;
        GalleryControlsGlass.Visibility = exhibition ? Visibility.Visible : Visibility.Collapsed;
        ExhibitionLibraryActions.Visibility = exhibition ? Visibility.Visible : Visibility.Collapsed;
        if (exhibition)
            GalleryControlsGlass.Background = TopBarTint.Background;
        TopBarTint.CornerRadius = TopBarGradient.CornerRadius = new CornerRadius(exhibition ? 18 : 14);
        TopBarGradient.BorderThickness = new Thickness(exhibition ? 0.5 : 1);
        StatusBarGlass.CornerRadius = new CornerRadius(exhibition ? 16 : 12);
        PreviewToolbar.Padding = exhibition ? new Thickness(12, 6, 12, 6) : new Thickness(14, 8, 14, 8);
        PreviewToolbar.Margin = exhibition ? new Thickness(20, 12, 20, 4) : new Thickness(0);
        PreviewToolbar.CornerRadius = new CornerRadius(exhibition ? 16 : 0);
        PreviewToolbar.BorderThickness = exhibition ? new Thickness(1) : new Thickness(0, 0, 0, 1);
        PreviewToolbar.Background = exhibition ? MakeViewerGlass() : new SolidColorBrush(Color.FromArgb(168, 23, 25, 29));
        PreviewTitleBar.FontSize = exhibition ? 15 : 14;
        DetailPanel.Margin = exhibition ? new Thickness(6, 10, 16, 16) : new Thickness(0);
        DetailPanel.CornerRadius = exhibition ? new CornerRadius(18) : new CornerRadius(16, 0, 0, 16);
        DetailPanel.BorderThickness = exhibition ? new Thickness(1) : new Thickness(1, 0, 0, 0);
        if (exhibition) DetailPanel.Background = MakeViewerGlass();
        PreviewFilmstripBar.Margin = exhibition ? new Thickness(8, 10, 4, 16) : new Thickness(0);
        PreviewFilmstripBar.CornerRadius = new CornerRadius(exhibition ? 14 : 0);
        PreviewFilmstripBar.Background = exhibition ? MakeViewerGlass() : new SolidColorBrush(Color.FromArgb(204, 16, 16, 20));
        ConfigureFilmstripLayout(exhibition);
        ApplyResponsivePreviewDetail();
        MapHeader.CornerRadius = new CornerRadius(exhibition ? 18 : 14);
        MapPlaceContext.CornerRadius = new CornerRadius(exhibition ? 18 : 14);
        MapPlaceContext.Padding = exhibition ? new Thickness(20, 16, 20, 16) : new Thickness(16, 12, 16, 12);
        MapPlaceContext.HorizontalAlignment = exhibition ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        MapPlaceContext.MaxWidth = exhibition ? 840 : double.PositiveInfinity;
        MapPlaceReel.MaxWidth = exhibition ? 1100 : double.PositiveInfinity;
        UpdateMapReelStyle();
        MapPlaceTitle.FontSize = exhibition ? 24 : 20;
        ArrangeLibraryHeader(TopBarOverlay.ActualWidth);
        ApplyFilmstripVisibility();
    }

    private static AcrylicBrush MakeViewerGlass() => new()
    {
        TintColor = Color.FromArgb(255, 20, 24, 29),
        TintOpacity = .78,
        FallbackColor = Color.FromArgb(245, 20, 24, 29),
    };

    private void UpdateMapReelStyle()
    {
        if (_settings.InterfaceStyle != 1)
        {
            MapPlaceReel.Width = double.NaN;
            MapPlaceReel.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }
        var available = MapPlaceOverlay.ActualWidth > 32 ? MapPlaceOverlay.ActualWidth - 32 : 1100;
        MapPlaceReel.Width = Math.Min(available, Math.Min(1100, Math.Max(220, _mapPlace.Count * 160 + 24)));
        MapPlaceReel.HorizontalAlignment = HorizontalAlignment.Left;
    }
}
