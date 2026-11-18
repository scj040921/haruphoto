using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private int? _appliedInterfaceStyle;
    private Brush? _classicRootBackground;
    private Brush? _classicMapHeaderBackground;
    private Brush? _classicMapHeaderBorder;
    private Brush? _classicDetailBackground;
    private Brush? _classicStatsSurfaceBackground;
    private Brush? _classicStatsSurfaceBorder;
    private Thickness _classicStatsSurfaceBorderThickness;
    private Thickness _classicStatsSurfacePadding;
    // The style changes presentation only. Existing theme and layout preferences stay intact.
    private void ApplyInterfaceStyle()
    {
        var exhibition = _settings.InterfaceStyle == 1;
        _classicRootBackground ??= RootSurface.Background;
        _classicMapHeaderBackground ??= MapHeader.Background;
        _classicMapHeaderBorder ??= MapHeader.BorderBrush;
        _classicDetailBackground ??= DetailPanel.Background;
        _classicStatsSurfaceBackground ??= StatsGridSurface.Background;
        _classicStatsSurfaceBorder ??= StatsGridSurface.BorderBrush;
        _classicStatsSurfaceBorderThickness = StatsGridSurface.BorderThickness;
        _classicStatsSurfacePadding = StatsGridSurface.Padding;
        RootSurface.Background = exhibition ? new SolidColorBrush(Color.FromArgb(255, 18, 19, 18)) : _classicRootBackground;
        NavView.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        NavBrandHeader.Padding = exhibition ? new Thickness(22, 22, 16, 26) : new Thickness(16, 8, 12, 14);
        NavBrandHeader.Spacing = exhibition ? 12 : 9;
        NavBrandMark.Width = NavBrandMark.Height = exhibition ? 28 : 20;
        NavBrandText.FontSize = exhibition ? 22 : 15;
        NavFooterPanel.Visibility = exhibition ? Visibility.Collapsed : Visibility.Visible;
        ExhibitionSettingsNav.Visibility = exhibition ? Visibility.Visible : Visibility.Collapsed;
        if (exhibition)
        {
            RootSurface.Resources["AppAccentBrush"] = new SolidColorBrush(Color.FromArgb(255, 196, 170, 112));
            RootSurface.Resources["LibraryHeaderBrush"] = MakeExhibitionGlass(Color.FromArgb(255, 30, 31, 29), .80);
            RootSurface.Resources["LibraryCardBrush"] = MakeExhibitionGlass(Color.FromArgb(255, 26, 27, 25), .72);
            RootSurface.Resources["LibraryHairlineBrush"] = new SolidColorBrush(Color.FromArgb(48, 229, 223, 205));
            RootSurface.Resources["LibraryPrimaryTextBrush"] = new SolidColorBrush(Color.FromArgb(255, 242, 239, 230));
            RootSurface.Resources["LibrarySecondaryTextBrush"] = new SolidColorBrush(Color.FromArgb(255, 177, 175, 164));
        }
        else
        {
            foreach (var key in new[] { "AppAccentBrush", "LibraryHeaderBrush", "LibraryCardBrush", "LibraryHairlineBrush", "LibraryPrimaryTextBrush", "LibrarySecondaryTextBrush" })
                RootSurface.Resources.Remove(key);
        }
        GalleryTitleText.FontSize = exhibition ? 24 : 19;
        LibraryNameText.FontSize = exhibition ? 13 : 12;
        NavView.OpenPaneLength = exhibition ? 250 : 210;
        if (_appliedInterfaceStyle != _settings.InterfaceStyle)
        {
            NavView.PaneDisplayMode = exhibition ? NavigationViewPaneDisplayMode.Top : NavigationViewPaneDisplayMode.Auto;
            NavView.IsPaneOpen = !exhibition;
            _appliedInterfaceStyle = _settings.InterfaceStyle;
        }
        ExhibitionLibraryActions.Visibility = exhibition ? Visibility.Visible : Visibility.Collapsed;
        if (exhibition)
        {
            TopBarTint.Background = MakeExhibitionGlass(Color.FromArgb(255, 29, 30, 28), .80);
            TopBarGradient.BorderBrush = new SolidColorBrush(Color.FromArgb(58, 229, 223, 205));
        }
        TopBarOverlay.Margin = exhibition ? new Thickness(28, 20, 28, 12) : new Thickness(20, 16, 20, 14);
        TopBarTint.CornerRadius = TopBarGradient.CornerRadius = new CornerRadius(exhibition ? 20 : 14);
        TopBarGradient.BorderThickness = new Thickness(1);
        ArrangeGalleryActions();
        StatusBarGlass.CornerRadius = new CornerRadius(exhibition ? 18 : 12);
        MainScroll.Padding = exhibition ? new Thickness(28, 0, 28, 22) : new Thickness(20, 0, 20, 16);
        StatsGridSurface.Background = exhibition ? new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) : _classicStatsSurfaceBackground;
        StatsGridSurface.BorderBrush = exhibition ? new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) : _classicStatsSurfaceBorder;
        StatsGridSurface.BorderThickness = exhibition ? new Thickness(0) : _classicStatsSurfaceBorderThickness;
        StatsGridSurface.Padding = exhibition ? new Thickness(0) : _classicStatsSurfacePadding;
        ApplyGalleryLayout();
        PreviewToolbar.Padding = exhibition ? new Thickness(12, 6, 12, 6) : new Thickness(14, 8, 14, 8);
        PreviewToolbar.Margin = exhibition ? new Thickness(22, 16, 22, 8) : new Thickness(0);
        PreviewToolbar.CornerRadius = new CornerRadius(exhibition ? 18 : 0);
        PreviewToolbar.BorderThickness = exhibition ? new Thickness(1) : new Thickness(0, 0, 0, 1);
        PreviewToolbar.Background = exhibition ? MakeViewerGlass() : new SolidColorBrush(Color.FromArgb(168, 23, 25, 29));
        PreviewTitleBar.FontSize = exhibition ? 17 : 14;
        DetailPanel.Margin = exhibition ? new Thickness(4, 10, 18, 16) : new Thickness(0);
        DetailPanel.CornerRadius = exhibition ? new CornerRadius(22) : new CornerRadius(16, 0, 0, 16);
        DetailPanel.BorderThickness = exhibition ? new Thickness(1) : new Thickness(1, 0, 0, 0);
        DetailPanel.Background = exhibition ? MakeViewerGlass() : _classicDetailBackground;
        PreviewFilmstripBar.Margin = exhibition ? new Thickness(10, 10, 4, 18) : new Thickness(0);
        PreviewFilmstripBar.CornerRadius = new CornerRadius(exhibition ? 16 : 0);
        PreviewFilmstripBar.Background = exhibition ? MakeViewerGlass() : new SolidColorBrush(Color.FromArgb(204, 16, 16, 20));
        ConfigureFilmstripLayout(exhibition);
        ApplyResponsivePreviewDetail();
        MapHeader.Margin = exhibition ? new Thickness(26, 22, 26, 8) : new Thickness(16, 12, 16, 8);
        MapHeader.Padding = exhibition ? new Thickness(22, 16, 22, 16) : new Thickness(18, 12, 18, 12);
        MapHeader.MaxWidth = exhibition ? 1360 : double.PositiveInfinity;
        MapHeader.HorizontalAlignment = exhibition ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        MapHeader.Background = exhibition ? MakeExhibitionGlass(Color.FromArgb(255, 20, 22, 21), .84) : _classicMapHeaderBackground;
        MapHeader.BorderBrush = exhibition ? new SolidColorBrush(Color.FromArgb(62, 229, 223, 205)) : _classicMapHeaderBorder;
        MapHeader.CornerRadius = new CornerRadius(exhibition ? 22 : 14);
        MapPlaceContext.CornerRadius = new CornerRadius(exhibition ? 24 : 14);
        MapPlaceContext.Padding = exhibition ? new Thickness(24, 18, 24, 18) : new Thickness(16, 12, 16, 12);
        MapPlaceContext.HorizontalAlignment = exhibition ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        MapPlaceContext.MaxWidth = exhibition ? 840 : double.PositiveInfinity;
        MapPlaceReel.MaxWidth = exhibition ? 1100 : double.PositiveInfinity;
        UpdateMapReelStyle();
        MapPlaceTitle.FontSize = exhibition ? 30 : 20;
        ArrangeLibraryHeader(TopBarOverlay.ActualWidth);
        ApplyFilmstripVisibility();
    }

    private static AcrylicBrush MakeViewerGlass() => MakeExhibitionGlass(Color.FromArgb(255, 20, 21, 20), .82);

    private static AcrylicBrush MakeExhibitionGlass(Color tint, double opacity) => new()
    {
        TintColor = tint,
        TintOpacity = opacity,
        FallbackColor = Color.FromArgb(248, tint.R, tint.G, tint.B),
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
