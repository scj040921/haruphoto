using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    // The style changes presentation only. Existing theme and layout preferences stay intact.
    private void ApplyInterfaceStyle()
    {
        var exhibition = _settings.InterfaceStyle == 1;
        GalleryOverline.Visibility = exhibition ? Visibility.Visible : Visibility.Collapsed;
        GalleryTitleText.FontSize = exhibition ? 26 : 19;
        GalleryTitleText.FontWeight = exhibition ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold;
        LibraryNameText.FontSize = exhibition ? 13 : 12;
        NavView.OpenPaneLength = exhibition ? 224 : 210;
        TopBarTint.CornerRadius = TopBarGradient.CornerRadius = new CornerRadius(exhibition ? 18 : 14);
        TopBarGradient.BorderThickness = new Thickness(exhibition ? 0.5 : 1);
        StatusBarGlass.CornerRadius = new CornerRadius(exhibition ? 16 : 12);
        PreviewToolbar.Padding = exhibition ? new Thickness(20, 10, 20, 10) : new Thickness(14, 8, 14, 8);
        PreviewToolbar.Background = new SolidColorBrush(exhibition ? Color.FromArgb(220, 13, 18, 25) : Color.FromArgb(168, 23, 25, 29));
        PreviewTitleBar.FontSize = exhibition ? 15 : 14;
        DetailPanel.CornerRadius = new CornerRadius(exhibition ? 22 : 16, 0, 0, exhibition ? 22 : 16);
        MapHeader.CornerRadius = new CornerRadius(exhibition ? 18 : 14);
        MapPlaceContext.CornerRadius = new CornerRadius(exhibition ? 18 : 14);
        MapPlaceContext.Padding = exhibition ? new Thickness(20, 16, 20, 16) : new Thickness(16, 12, 16, 12);
        MapPlaceTitle.FontSize = exhibition ? 24 : 20;
        ArrangeLibraryHeader(TopBarOverlay.ActualWidth);
    }
}
