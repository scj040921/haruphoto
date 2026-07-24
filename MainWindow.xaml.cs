using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.System;

namespace PhotoAlbum;

public sealed partial class MainWindow : Window
{
    private const int PageSize = 100;

    private readonly ObservableCollection<PhotoItem> _photos = new();
    private readonly List<PhotoItem> _allPhotos = new();
    private List<PhotoItem> _currentView = new();
    private readonly ThumbnailService _thumbService = new();
    private CancellationTokenSource? _thumbCts;
    private readonly FolderWatcherService _folderWatcher = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _saveTimer;

    private int _currentPage, _sortBy;
    private string _searchKeyword = "";
    private bool _favoritesOnly;
    private string _categoryFilter = "";  // "" = 不筛选
    private bool _uiReady;
    private bool _importing;

    private int _previewIndex = -1;
    private bool _suppressRatingEvent;
    private bool _suppressCategoryEvent;

    // ── 分类管理 ──
    private readonly List<string> _categories = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "haruphoto";

        // 应用深色模式主题
        try
        {
            var root = Content as FrameworkElement;
            if (root != null)
                root.RequestedTheme = _settings.DarkMode ? ElementTheme.Dark : ElementTheme.Light;
        }
        catch { }

        // 应用毛玻璃效果
        ApplyGlassEffects();

        // 设置窗口图标
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch { }

        // 最小窗口尺寸
        try
        {
            var presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 640;
            AppWindow.SetPresenter(presenter);
        }
        catch { }

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); _currentPage = 0; RefreshPhotos(); };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += async (s, e) => { _saveTimer.Stop(); await LibraryStore.SaveAsync(_allPhotos); };

        _folderWatcher.NewFilesFound += OnNewFilesFound;
        PhotoGrid.ItemsSource = _photos;

        Closed += (s, e) =>
        {
            _saveTimer.Stop();
            _folderWatcher.Stop();
            try { LibraryStore.SaveAsync(_allPhotos).Wait(2000); } catch { }
        };

        _uiReady = true;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在加载照片库…";
        try
        {
            var (photos, missing) = await LibraryStore.LoadAsync();
            _allPhotos.AddRange(photos);
            _sortBy = _settings.SortMode;
            SortCombo.SelectedIndex = _sortBy;

            RebuildCategories();
            RefreshPhotos();

            StatusText.Text = _allPhotos.Count == 0
                ? "点击左下角「＋ 导入文件夹」开始"
                : missing > 0
                    ? $"已加载 {photos.Count} 张照片（{missing} 张文件缺失已跳过）"
                    : $"已加载 {photos.Count} 张照片";

            _folderWatcher.Start(_settings.AutoScanIntervalMinutes, _settings.WatchedFolders);
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
            StatusText.Text = _allPhotos.Count == 0
                ? "点击左下角「＋ 导入文件夹」开始"
                : "就绪";
        }
    }

    // ══════════ 毛玻璃 & 动画 ══════════

    private void ApplyGlassEffects()
    {
        // 预览详情面板毛玻璃
        if (DetailPanel != null)
        {
            var isDark = _settings.DarkMode;
            DetailPanel.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(isDark ? (byte)153 : (byte)180, isDark ? (byte)20 : (byte)245, isDark ? (byte)20 : (byte)245, isDark ? (byte)30 : (byte)255));
            DetailPanel.BorderBrush = new SolidColorBrush(
                Windows.UI.Color.FromArgb(isDark ? (byte)30 : (byte)40, (byte)255, (byte)255, (byte)255));
        }
    }

    private static void AnimateFadeIn(UIElement element, double from = 0, double to = 1, int delayMs = 0, double durationMs = 400)
    {
        element.Opacity = from;
        element.Visibility = Visibility.Visible;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from, To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();
    }

    private static void AnimateSlideIn(UIElement element, double fromY, double toY = 0, int durationMs = 350)
    {
        var transform = element.RenderTransform as Microsoft.UI.Xaml.Media.CompositeTransform;
        if (transform == null)
        {
            transform = new Microsoft.UI.Xaml.Media.CompositeTransform();
            element.RenderTransform = transform;
        }
        transform.TranslateY = fromY;
        element.Opacity = 0;

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = fromY, To = toY,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        sb.Children.Add(slide);

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        element.Visibility = Visibility.Visible;
        sb.Begin();
    }

    private void AnimatePreviewEnter()
    {
        var overlay = PreviewOverlay;
        var transform = new Microsoft.UI.Xaml.Media.CompositeTransform { TranslateY = 80 };
        overlay.RenderTransform = transform;
        overlay.Opacity = 0;

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 80, To = 0,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, overlay);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        sb.Children.Add(slide);

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.6, To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, overlay);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        sb.Begin();
    }

    private void AnimatePhotoCards()
    {
        var grid = PhotoGrid;
        grid.Opacity = 0;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, grid);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();
    }

    // ══════════ 分类 ══════════

    /// <summary>从照片数据重建分类列表 + 导航栏分类项</summary>
    private void RebuildCategories()
    {
        _categories.Clear();
        var cats = _allPhotos
            .Where(p => !string.IsNullOrEmpty(p.Category))
            .Select(p => p.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _categories.AddRange(cats);
        RebuildCategoryNavItems();
    }

    /// <summary>刷新预览面板分类 ComboBox</summary>
    private void UpdatePreviewCategoryCombo()
    {
        if (PreviewCategoryCombo == null) return;
        var selIdx = PreviewCategoryCombo.SelectedIndex;
        PreviewCategoryCombo.Items.Clear();
        PreviewCategoryCombo.Items.Add("(未分类)");
        foreach (var cat in _categories)
            PreviewCategoryCombo.Items.Add(cat);
        PreviewCategoryCombo.SelectedIndex = Math.Min(selIdx, PreviewCategoryCombo.Items.Count - 1);
    }

    /// <summary>重建导航栏中的分类子项</summary>
    private void RebuildCategoryNavItems()
    {
        // 找到"分类"父级 NavigationViewItem
        var catParent = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(nvi => nvi.Content?.ToString() == "分类");
        if (catParent == null) return;

        catParent.MenuItems.Clear();

        // "全部分类" 项
        var allItem = new NavigationViewItem
        {
            Content = "全部分类",
            Tag = "CatAll",
        };
        allItem.IsSelected = _categoryFilter == "";
        catParent.MenuItems.Add(allItem);

        // 动态分类项
        foreach (var cat in _categories)
        {
            var item = new NavigationViewItem
            {
                Content = cat,
                Tag = "Cat:" + cat,
            };
            item.IsSelected = _categoryFilter == cat;
            catParent.MenuItems.Add(item);
        }
    }

    /// <summary>管理分类对话框 —— 新建/重命名/删除</summary>
    private async void ManageCategories_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };

        var header = new TextBlock
        {
            Text = "管理分类",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16,
        };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 300,
            Content = BuildCategoryListPanel(),
        });

        // 新建分类
        var newBox = new TextBox { PlaceholderText = "输入新分类名称…", MinWidth = 200 };
        var addBtn = new Button { Content = "＋ 新建", Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6) };
        var addRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8 };
        addRow.Children.Add(newBox);
        addRow.Children.Add(addBtn);

        panel.Children.Add(addRow);

        var dlg = new ContentDialog
        {
            Title = "🏷 分类管理",
            Content = panel,
            CloseButtonText = "完成",
            XamlRoot = Content.XamlRoot,
        };

        addBtn.Click += (_, _) =>
        {
            var name = newBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (_categories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                StatusText.Text = $"分类「{name}」已存在";
                return;
            }
            _categories.Add(name);
            newBox.Text = "";
            RebuildCategoryNavItems();
            UpdatePreviewCategoryCombo();

            // 刷新列表（scroll viewer 是 panel 的第一个子元素）
            if (dlg.Content is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is ScrollViewer sv)
                sv.Content = BuildCategoryListPanel();
        };

        await dlg.ShowAsync();
    }

    /// <summary>构建分类列表（带重命名/删除按钮）</summary>
    private StackPanel BuildCategoryListPanel()
    {
        var list = new StackPanel { Spacing = 6 };

        if (_categories.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "暂无分类，导入照片后可在下方新建分类",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            });
            return list;
        }

        foreach (var cat in _categories.ToList())
        {
            var count = _allPhotos.Count(p => p.Category == cat);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });

            var label = new TextBlock
            {
                Text = $"{cat}（{count} 张）",
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                FontSize = 13,
            };
            Grid.SetColumn(label, 0);

            var renameBtn = new Button
            {
                Content = "✏",
                Padding = new Microsoft.UI.Xaml.Thickness(8, 4, 8, 4),
                Tag = cat,
            };
            Grid.SetColumn(renameBtn, 1);

            var delBtn = new Button
            {
                Content = "🗑",
                Padding = new Microsoft.UI.Xaml.Thickness(8, 4, 8, 4),
                Tag = cat,
            };
            Grid.SetColumn(delBtn, 2);

            renameBtn.Click += async (_, _) =>
            {
                var oldName = renameBtn.Tag as string ?? "";
                var inputBox = new TextBox { Text = oldName, MinWidth = 200 };
                var renameDlg = new ContentDialog
                {
                    Title = "重命名分类",
                    Content = inputBox,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    XamlRoot = Content.XamlRoot,
                };
                if (await renameDlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    var newName = inputBox.Text?.Trim();
                    if (string.IsNullOrEmpty(newName) || newName == oldName) return;
                    // 更新所有照片的分类
                    foreach (var p in _allPhotos.Where(p => p.Category == oldName))
                        p.Category = newName;
                    var idx = _categories.IndexOf(oldName);
                    if (idx >= 0) _categories[idx] = newName;
                    RebuildCategoryNavItems();
                    ScheduleSave();
                    RefreshPhotos();
                    UpdatePreviewCategoryCombo();
                }
            };

            delBtn.Click += (_, _) =>
            {
                var name = delBtn.Tag as string ?? "";
                foreach (var p in _allPhotos.Where(p => p.Category == name))
                    p.Category = "";
                _categories.Remove(name);
                RebuildCategoryNavItems();
                ScheduleSave();
                RefreshPhotos();
                UpdatePreviewCategoryCombo();
            };

            row.Children.Add(label);
            row.Children.Add(renameBtn);
            row.Children.Add(delBtn);
            list.Children.Add(row);
        }
        return list;
    }

    // ══════════ 筛选 / 排序 / 分页 ══════════

    private void RefreshPhotos()
    {
        if (!_uiReady) return;

        IEnumerable<PhotoItem> q = _allPhotos;
        if (_favoritesOnly) q = q.Where(p => p.IsFavorite);
        if (!string.IsNullOrEmpty(_categoryFilter))
            q = q.Where(p => p.Category == _categoryFilter);
        if (!string.IsNullOrWhiteSpace(_searchKeyword))
            q = q.Where(p => p.Filename.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase));

        q = _sortBy switch
        {
            1 => q.OrderBy(p => p.Filename, StringComparer.CurrentCultureIgnoreCase),
            2 => q.OrderByDescending(p => p.Rating).ThenByDescending(p => p.DateAdded),
            _ => q.OrderByDescending(p => p.DateAdded),
        };

        _currentView = q.ToList();
        var maxPage = Math.Max(0, (_currentView.Count - 1) / PageSize);
        _currentPage = Math.Clamp(_currentPage, 0, maxPage);

        _photos.Clear();
        foreach (var p in _currentView.Skip(_currentPage * PageSize).Take(PageSize))
            _photos.Add(p);

        PageInfo.Text = $"第 {_currentPage + 1}/{maxPage + 1} 页 · 共 {_currentView.Count} 张";
        PrevPageBtn.IsEnabled = _currentPage > 0;
        NextPageBtn.IsEnabled = _currentPage < maxPage;
        UpdateStats();
        AnimatePhotoCards();
    }

    private void UpdateStats()
    {
        var fav = _allPhotos.Count(p => p.IsFavorite);
        StatsText.Text = $"{_allPhotos.Count} 张照片 · {fav} 收藏";
    }

    private void LoadThumbnailsForCurrentPage()
    {
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        var cts = _thumbCts = new CancellationTokenSource();
        foreach (var p in _photos)
        {
            if (p.ThumbnailSource != null) continue;
            _ = LoadThumbnailAsync(p, cts.Token);
        }
    }

    private async Task LoadThumbnailAsync(PhotoItem photo, CancellationToken ct)
    {
        try
        {
            var img = await _thumbService.GetThumbnailAsync(photo.FilePath, ct);
            if (img != null && !ct.IsCancellationRequested)
                photo.ThumbnailSource = img;
        }
        catch (OperationCanceledException) { }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ══════════ 工具栏 ══════════

    private void NavView_SelectionChanged(NavigationView s, NavigationViewSelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        if (e.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString() ?? "";

            if (tag == "All")
            {
                _favoritesOnly = false;
                _categoryFilter = "";
                FavOnlyCheckBox.IsChecked = false;
            }
            else if (tag == "Favorites")
            {
                _favoritesOnly = true;
                _categoryFilter = "";
                FavOnlyCheckBox.IsChecked = true;
            }
            else if (tag == "CatAll")
            {
                _categoryFilter = "";
                _favoritesOnly = false;
                FavOnlyCheckBox.IsChecked = false;
            }
            else if (tag.StartsWith("Cat:"))
            {
                _categoryFilter = tag[4..];
                _favoritesOnly = false;
                FavOnlyCheckBox.IsChecked = false;
            }
            else return;

            _currentPage = 0;
            RefreshPhotos();
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox s, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!_uiReady) return;
        _searchKeyword = s.Text ?? "";
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SortCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _sortBy = Math.Max(0, SortCombo.SelectedIndex);
        _currentPage = 0;
        RefreshPhotos();
    }

    private void FavOnly_Changed(object s, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _favoritesOnly = FavOnlyCheckBox.IsChecked ?? false;
        _currentPage = 0;
        RefreshPhotos();
    }

    private void PrevPage_Click(object s, RoutedEventArgs e)
    {
        if (_currentPage > 0) { _currentPage--; RefreshPhotos(); }
    }

    private void NextPage_Click(object s, RoutedEventArgs e)
    {
        if (_currentPage < Math.Max(0, (_currentView.Count - 1) / PageSize)) { _currentPage++; RefreshPhotos(); }
    }

    /// <summary>设置按钮</summary>
    private async void SettingsButton_Click(object s, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 420 };

        var themeLabel = new TextBlock { Text = "外观主题", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 };
        var darkToggle = new ToggleSwitch { Header = "深色模式", IsOn = _settings.DarkMode, OnContent = "🌙 深色", OffContent = "☀️ 浅色" };

        var autoLabel = new TextBlock { Text = "自动扫描", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) };
        var autoToggle = new ToggleSwitch { Header = "自动检测新增图片", IsOn = _settings.AutoScan, OnContent = "已开启", OffContent = "已关闭" };
        var intervalBox = new NumberBox { Header = "扫描间隔（分钟）", Value = _settings.AutoScanIntervalMinutes, Minimum = 1, Maximum = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, IsEnabled = _settings.AutoScan };
        autoToggle.Toggled += (_, _) => { intervalBox.IsEnabled = autoToggle.IsOn; };

        var watchLabel = new TextBlock { Text = $"已监控 {_settings.WatchedFolders.Count} 个文件夹", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };

        panel.Children.Add(themeLabel);
        panel.Children.Add(darkToggle);
        panel.Children.Add(autoLabel);
        panel.Children.Add(autoToggle);
        panel.Children.Add(intervalBox);
        panel.Children.Add(watchLabel);

        var dlg = new ContentDialog { Title = "⚙ 设置", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var oldDark = _settings.DarkMode;
            _settings.DarkMode = darkToggle.IsOn;
            _settings.AutoScan = autoToggle.IsOn;
            _settings.AutoScanIntervalMinutes = Math.Max(1, (int)intervalBox.Value);
            _settings.Save();

            if (_settings.DarkMode != oldDark)
            {
                var rd = new ContentDialog { Title = "主题已更改", Content = "需要重启应用以应用新主题。", PrimaryButtonText = "立即重启", CloseButtonText = "稍后", XamlRoot = Content.XamlRoot };
                if (await rd.ShowAsync() == ContentDialogResult.Primary)
                {
                    var newWin = new MainWindow();
                    newWin.Activate();
                    Close();
                    return;
                }
            }

            if (_settings.AutoScan) _folderWatcher.Start(_settings.AutoScanIntervalMinutes, _settings.WatchedFolders);
            else _folderWatcher.Stop();
            StatusText.Text = "设置已保存";
        }
    }

    // ══════════ 照片卡片 ══════════

    private void PhotoCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItem photo)
            OpenPreview(photo);
    }

    private void CardFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItem photo) return;
        photo.IsFavorite = !photo.IsFavorite;
        UpdateStats();
        StatusText.Text = $"{photo.Filename}：{(photo.IsFavorite ? "★ 已收藏" : "已取消收藏")}";
        ScheduleSave();
        if (_favoritesOnly && !photo.IsFavorite) RefreshPhotos();
    }

    // ══════════ 预览 ══════════

    private void OpenPreview(PhotoItem photo)
    {
        var idx = _currentView.IndexOf(photo);
        if (idx < 0) return;
        PreviewOverlay.Visibility = Visibility.Visible;
        AnimatePreviewEnter();
        ShowPreviewAt(idx);
    }

    private void ShowPreviewAt(int idx)
    {
        if (_currentView.Count == 0) { ClosePreview(); return; }
        _previewIndex = Math.Clamp(idx, 0, _currentView.Count - 1);
        var p = _currentView[_previewIndex];

        PreviewName.Text = p.Filename;
        PreviewInfo.Text = $"{p.SizeText} · 添加于 {p.DateText}";
        PreviewPath.Text = p.FilePath;
        PreviewFavButton.Content = p.IsFavorite ? "★ 取消收藏" : "☆ 收藏";

        _suppressRatingEvent = true;
        PreviewRating.Value = p.Rating;
        _suppressRatingEvent = false;

        // ── 分类 ComboBox ──
        _suppressCategoryEvent = true;
        PreviewCategoryCombo.Items.Clear();
        PreviewCategoryCombo.Items.Add("(未分类)");  // 清除分类
        foreach (var cat in _categories)
            PreviewCategoryCombo.Items.Add(cat);
        // 选中当前分类
        var catIdx = string.IsNullOrEmpty(p.Category) ? 0 : _categories.IndexOf(p.Category) + 1;
        if (catIdx < 0) catIdx = 0;
        PreviewCategoryCombo.SelectedIndex = catIdx;
        _suppressCategoryEvent = false;

        PreviewPrevBtn.IsEnabled = _previewIndex > 0;
        PreviewNextBtn.IsEnabled = _previewIndex < _currentView.Count - 1;

        PreviewLoading.Visibility = Visibility.Visible;
        var bmp = new BitmapImage { DecodePixelWidth = 1600 };
        bmp.ImageOpened += (_, _) => PreviewLoading.Visibility = Visibility.Collapsed;
        bmp.ImageFailed += (_, _) => PreviewLoading.Visibility = Visibility.Collapsed;
        PreviewImage.Source = bmp;
        bmp.UriSource = new Uri(p.FilePath);
    }

    private void ClosePreview()
    {
        PreviewOverlay.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        _previewIndex = -1;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (PreviewOverlay.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case VirtualKey.Escape: ClosePreview(); e.Handled = true; break;
            case VirtualKey.Left: if (PreviewPrevBtn.IsEnabled) { ShowPreviewAt(_previewIndex - 1); e.Handled = true; } break;
            case VirtualKey.Right: if (PreviewNextBtn.IsEnabled) { ShowPreviewAt(_previewIndex + 1); e.Handled = true; } break;
        }
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();
    private void PreviewPrev_Click(object sender, RoutedEventArgs e) => ShowPreviewAt(_previewIndex - 1);
    private void PreviewNext_Click(object sender, RoutedEventArgs e) => ShowPreviewAt(_previewIndex + 1);

    private void PreviewFav_Click(object sender, RoutedEventArgs e)
    {
        if (_previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        var p = _currentView[_previewIndex];
        p.IsFavorite = !p.IsFavorite;
        PreviewFavButton.Content = p.IsFavorite ? "★ 取消收藏" : "☆ 收藏";
        UpdateStats();
        ScheduleSave();
        if (_favoritesOnly && !p.IsFavorite) { RefreshPhotos(); ShowPreviewAt(_previewIndex); }
    }

    private void PreviewRating_ValueChanged(RatingControl sender, object args)
    {
        if (_suppressRatingEvent || _previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        var p = _currentView[_previewIndex];
        var newVal = (int)Math.Round(sender.Value);
        if (newVal == p.Rating) return;
        p.Rating = newVal;
        ScheduleSave();
        if (_sortBy == 2) { RefreshPhotos(); ShowPreviewAt(_previewIndex); }
    }

    /// <summary>分类选择变更</summary>
    private void PreviewCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCategoryEvent || _previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        var p = _currentView[_previewIndex];
        var selIdx = PreviewCategoryCombo.SelectedIndex;
        var newCat = selIdx <= 0 ? "" : (PreviewCategoryCombo.SelectedItem as string ?? "");
        if (newCat == "(未分类)") newCat = "";
        if (p.Category == newCat) return;
        p.Category = newCat;
        RebuildCategories();
        ScheduleSave();
        RefreshPhotos();
        StatusText.Text = string.IsNullOrEmpty(newCat) ? $"已清除「{p.Filename}」的分类" : $"已将「{p.Filename}」归入「{newCat}」";
    }

    private void RevealInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        try { Process.Start("explorer.exe", $"/select,\"{_currentView[_previewIndex].FilePath}\""); } catch { }
    }

    private async void ShareImage_Click(object sender, RoutedEventArgs e)
    {
        if (_previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        await CopyToClipboardAsync(_currentView[_previewIndex].FilePath);
        StatusText.Text = $"📋 已复制：{Path.GetFileName(_currentView[_previewIndex].FilePath)}";
    }

    private async void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (_previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        await CopyToClipboardAsync(_currentView[_previewIndex].FilePath);
        StatusText.Text = "📋 已复制到剪贴板";
    }

    private static async Task CopyToClipboardAsync(string filePath)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
            if (file != null)
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
        }
        catch { }
    }

    private async void RemoveFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        var p = _currentView[_previewIndex];
        var dlg = new ContentDialog { Title = "从图库移除", Content = $"将「{p.Filename}」从图库移除？\n磁盘上的文件不会被删除。", PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _allPhotos.Remove(p);
        RefreshPhotos();
        ScheduleSave();
        StatusText.Text = $"已移除 {p.Filename}（文件仍在磁盘上）";
        ShowPreviewAt(_previewIndex);
    }

    // ══════════ 批量操作 ══════════

    private void BatchFav_Click(object s, RoutedEventArgs e)
    {
        if (_currentView.Count == 0) { StatusText.Text = "当前筛选结果为空"; return; }
        foreach (var p in _currentView) p.IsFavorite = true;
        UpdateStats();
        ScheduleSave();
        StatusText.Text = $"已收藏当前筛选结果（{_currentView.Count} 张）";
    }

    private async void BatchDelete_Click(object s, RoutedEventArgs e)
    {
        if (_allPhotos.Count == 0) { StatusText.Text = "图库为空"; return; }
        var dlg = new ContentDialog { Title = "清空图库", Content = $"将从图库移除全部 {_allPhotos.Count} 张照片。\n磁盘上的文件不会被删除。", PrimaryButtonText = "清空图库", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _thumbCts?.Cancel();
        ClosePreview();
        _allPhotos.Clear();
        RefreshPhotos();
        ScheduleSave();
        StatusText.Text = "图库已清空（文件仍在磁盘上）";
    }

    // ══════════ 导入 ══════════

    private async void ImportButton_Click(object s, RoutedEventArgs e)
    {
        if (_importing) return;

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _importing = true;
        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在扫描文件夹…";

        var rootPath = folder.Path;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff" };
        var known = new HashSet<string>(_allPhotos.Select(p => p.FilePath), StringComparer.OrdinalIgnoreCase);

        try
        {
            var found = await Task.Run(() =>
            {
                var list = new List<(string Path, long Size)>();
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.System };
                var scanned = 0;
                foreach (var f in Directory.EnumerateFiles(rootPath, "*", options))
                {
                    scanned++;
                    if (scanned % 200 == 0) { var n = scanned; var c = list.Count; DispatcherQueue.TryEnqueue(() => StatusText.Text = $"正在扫描… 已检查 {n} 个文件，发现 {c} 张新照片"); }
                    if (!exts.Contains(Path.GetExtension(f))) continue;
                    if (known.Contains(f)) continue;
                    try { var fi = new FileInfo(f); list.Add((fi.FullName, fi.Length)); } catch { }
                }
                return list;
            });

            foreach (var f in found)
            {
                _allPhotos.Add(new PhotoItem { Filename = Path.GetFileName(f.Path), FilePath = f.Path, FileSize = f.Size, DateAdded = DateTime.Now });
            }

            if (!_settings.WatchedFolders.Contains(rootPath, StringComparer.OrdinalIgnoreCase))
            {
                _settings.WatchedFolders.Add(rootPath);
                _settings.Save();
                _folderWatcher.Start(_settings.AutoScanIntervalMinutes, _settings.WatchedFolders);
            }

            RebuildCategories();
            RefreshPhotos();
            ScheduleSave();
            StatusText.Text = $"导入完成：新增 {found.Count} 张";
        }
        catch (Exception ex) { StatusText.Text = "导入失败：" + ex.Message; }
        finally { BusyRing.Visibility = Visibility.Collapsed; _importing = false; }
    }

    // ══════════ 自动扫描 ══════════

    private void OnNewFilesFound(List<string> files)
    {
        var known = new HashSet<string>(_allPhotos.Select(p => p.FilePath), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var path in files)
        {
            if (known.Contains(path)) continue;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) continue;
                _allPhotos.Add(new PhotoItem { Filename = fi.Name, FilePath = fi.FullName, FileSize = fi.Length, DateAdded = DateTime.Now });
                added++;
            }
            catch { }
        }

        if (added > 0)
        {
            RebuildCategories();
            RefreshPhotos();
            ScheduleSave();
            StatusText.Text = $"🔍 自动发现 {added} 张新照片";
        }
    }
}
