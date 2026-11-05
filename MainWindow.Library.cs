using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private enum LibrarySection { Gallery, Statistics, Organize }
    private bool _synchronizingLibraryNavigation;
    private LibrarySection? _visibleLibrarySection;
    private Storyboard? _librarySectionAnimation;

    // 将图库顶栏、照片和底栏作为一个页面切换，避免其他页面与图库工具交叉显示。
    private void ShowLibrarySection(LibrarySection section)
    {
        var changed = _visibleLibrarySection.HasValue && _visibleLibrarySection != section;
        _visibleLibrarySection = section;
        _librarySectionAnimation?.Stop();
        GallerySurface.Opacity = StatsScroll.Opacity = OrganizeScroll.Opacity = 1;
        var gallery = section == LibrarySection.Gallery;
        GallerySurface.Visibility = gallery ? Visibility.Visible : Visibility.Collapsed;
        MainScroll.Visibility = gallery ? Visibility.Visible : Visibility.Collapsed;
        StatsScroll.Visibility = section == LibrarySection.Statistics ? Visibility.Visible : Visibility.Collapsed;
        OrganizeScroll.Visibility = section == LibrarySection.Organize ? Visibility.Visible : Visibility.Collapsed;
        if (changed && MotionEnabled)
        {
            UIElement target = section switch { LibrarySection.Statistics => StatsScroll, LibrarySection.Organize => OrganizeScroll, _ => GallerySurface };
            target.Opacity = .82;
            var fade = new DoubleAnimation { From = .82, To = 1, Duration = TimeSpan.FromMilliseconds(Math.Clamp(_settings.AnimationDurationMs * .6, 100, 220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fade, target); Storyboard.SetTargetProperty(fade, "Opacity");
            _librarySectionAnimation = new Storyboard(); _librarySectionAnimation.Children.Add(fade); _librarySectionAnimation.Begin();
        }
        PageInfoButton.Flyout.Hide();
        FilterMenuButton.Flyout.Hide();
        if (!gallery) EmptyState.Visibility = Visibility.Collapsed;
        else if ((NavView.SelectedItem as NavigationViewItem)?.Tag is "Stats" or "Organize")
        {
            // 整理结果属于图库；只同步侧栏高亮，不触发导航事件清掉刚选好的筛选。
            _synchronizingLibraryNavigation = true;
            try { NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "All"); }
            finally { _synchronizingLibraryNavigation = false; }
        }
    }

    private void UpdateLibraryHeading()
    {
        GalleryTitleText.Text = !string.IsNullOrEmpty(_categoryFilter)
            ? (_categoryFilter == "__UNCATEGORIZED__" ? "未分类" : _categoryFilter)
            : !string.IsNullOrEmpty(_timelineFilter) ? _timelineFilter
            : _favoritesOnly ? "收藏" : "所有照片";
        LibraryNameText.Text = $"{_currentView.Count:N0} 张照片";
    }

    private void GalleryHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ArrangeLibraryHeader(e.NewSize.Width);
        // 只为第一屏预留顶栏的实际高度；滚动时照片可继续从悬浮顶栏下方经过。
        if (GalleryContentGrid != null && e.NewSize.Height > 0)
        {
            var inset = Math.Ceiling(TopBarOverlay.Margin.Top + e.NewSize.Height + TopBarOverlay.Margin.Bottom);
            if (Math.Abs(GalleryContentGrid.Margin.Top - inset) > 0.5)
                GalleryContentGrid.Margin = new Thickness(0, inset, 0, 0);
        }
    }

    // Application.Resources 的直接查找跟随应用主题，可能与窗口单独选择的深浅色不同。
    // 代码生成的卡片显式读取当前窗口对应的调色板；XAML 仍使用 ThemeResource。
    private Brush LibraryThemeBrush(string key)
    {
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[IsDark() ? "Dark" : "Light"];
        return (Brush)palette[key];
    }

    private Brush LibraryChartBrush()
    {
        var color = _settings.GetAccentColor();
        if (IsDark()) color = Windows.UI.Color.FromArgb(255,
            (byte)(color.R * 0.55 + 255 * 0.45), (byte)(color.G * 0.55 + 255 * 0.45), (byte)(color.B * 0.55 + 255 * 0.45));
        return new SolidColorBrush(color);
    }

    private void ArrangeLibraryHeader(double width)
    {
        if (GalleryFilters == null || SearchBox == null || width <= 0) return;
        var singleLine = width >= 820;
        var compact = width < 520;
        var stackedSearch = width < 580;
        Grid.SetColumnSpan(HeaderTitleRow, singleLine ? 1 : 2);
        Grid.SetRow(HeaderControlRow, singleLine ? 0 : 1);
        Grid.SetColumn(HeaderControlRow, singleLine ? 1 : 0);
        Grid.SetColumnSpan(HeaderControlRow, singleLine ? 1 : 2);
        HeaderTitleRow.Margin = singleLine ? new Thickness(16, 12, 12, 12) : new Thickness(16, 12, 16, 0);
        HeaderControlRow.Margin = singleLine ? new Thickness(0, 12, 16, 12) : new Thickness(16, 10, 16, 12);
        HeaderTitleRow.RowSpacing = stackedSearch ? 12 : 0;
        HeaderControlRow.RowSpacing = compact ? 12 : 0;
        Grid.SetRow(SearchBox, stackedSearch ? 1 : 0);
        Grid.SetColumn(SearchBox, stackedSearch ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, stackedSearch ? 2 : 1);
        Grid.SetRow(SearchGlyph, stackedSearch ? 1 : 0);
        Grid.SetColumn(SearchGlyph, stackedSearch ? 0 : 1);
        Grid.SetColumnSpan(SearchGlyph, stackedSearch ? 2 : 1);
        SearchBox.Width = stackedSearch ? double.NaN : singleLine ? 210 : 280;
        SearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;

        Grid.SetColumnSpan(GalleryFilters, compact ? 2 : 1);
        Grid.SetRow(PaginationGroup, compact ? 1 : 0);
        Grid.SetColumn(PaginationGroup, compact ? 0 : 1);
        Grid.SetColumnSpan(PaginationGroup, compact ? 2 : 1);
    }

    private void FilterDone_Click(object sender, RoutedEventArgs e) => FilterMenuButton.Flyout.Hide();

    private void StatsPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ArrangeStatsCards(e.NewSize.Width);

    private void ArrangeStatsCards(double width)
    {
        var columns = width > 0 && width < 600 ? 2 : 3;
        StatsGrid.ColumnDefinitions[2].Width = columns == 2 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        for (var i = 0; i < StatsGrid.Children.Count; i++)
        {
            Grid.SetRow((FrameworkElement)StatsGrid.Children[i], i / columns);
            Grid.SetColumn((FrameworkElement)StatsGrid.Children[i], i % columns);
            if (StatsGrid.Children[i] is Border card)
                card.BorderThickness = new Thickness(0, 0, 0, i / columns < (StatsGrid.Children.Count - 1) / columns ? 1 : 0);
        }
    }
}
