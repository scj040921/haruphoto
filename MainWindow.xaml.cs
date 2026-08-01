using Microsoft.UI.Input;
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
using Windows.UI.Core;

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
    private readonly HashSet<string> _selectedPaths = new();
    private bool _batchMode;
    private bool _cancelCatMode; // 取消分类模式：只允许选已分类照片

    private int _currentPage, _sortBy;
    private string _searchKeyword = "";
    private bool _favoritesOnly;
    private string _categoryFilter = "";  // "" = 不筛选
    private string _timelineFilter = "";  // "yyyy-MM"，"" = 不限
    private int _minRatingFilter;          // 0 = 不限
    private int _dateFilterDays;           // 0 = 不限
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

        // 应用主题 + 外观（深色/浅色即时生效）
        ApplyTheme();

        // 模板加载完成后重新应用外观（ContentGrid 此时才可查找）
        if (Content is FrameworkElement rootEl)
            rootEl.Loaded += (_, _) => ApplyAppearance();

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

            // 启动后后台补读缺失的拍摄日期（历史照片从未读过 EXIF）
            _ = EnrichMissingDateTakenAsync();
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
        // 预览详情面板毛玻璃 — 永远在深色遮罩上，使用固定半透明白色
        if (DetailPanel != null)
        {
            var isDark = _settings.DarkMode;
            DetailPanel.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(isDark ? (byte)153 : (byte)180, isDark ? (byte)20 : (byte)245, isDark ? (byte)20 : (byte)245, isDark ? (byte)30 : (byte)255));
            // 边框：半透明白色（在深色遮罩上始终可见）
            DetailPanel.BorderBrush = new SolidColorBrush(
                Windows.UI.Color.FromArgb(50, 255, 255, 255));
        }
    }

    /// <summary>背景透出模式下，将侧边栏设为半透明基底（保证文字可读 + 看到背景）</summary>
    private void ApplyTranslucentSurfaces()
    {
        try
        {
            var dark = _settings.DarkMode;
            // 内容区透明化（属性级赋值，切主题即时生效），让背景图/纯色背景透出
            var contentGrid = FindNavContentGrid();
            if (contentGrid != null)
                contentGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(0xA0, 20, 20, 24)
                    : Windows.UI.Color.FromArgb(0xCC, 250, 250, 251));
            // 阴影接收层同步半透明
            if (PhotoShadowReceiver != null)
                PhotoShadowReceiver.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                    ? Windows.UI.Color.FromArgb(0xA0, 20, 20, 24)
                    : Windows.UI.Color.FromArgb(0xCC, 250, 250, 251));

            // 侧边栏半透明基底（直接赋值 NavView.Background，属性级动态生效）
            var paneColor = dark
                ? Windows.UI.Color.FromArgb(0xB8, 24, 24, 28)
                : Windows.UI.Color.FromArgb(0xD9, 248, 248, 249);
            NavView.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(paneColor);
        }
        catch { }
    }

    /// <summary>查找 NavigationView 模板内的内容区 ContentGrid（SplitView.Content 的宿主）。
    /// 直接设置其 Background（属性级，切主题即时生效），绕开模板 Setter 静态解析问题</summary>
    private Microsoft.UI.Xaml.Controls.Grid FindNavContentGrid()
    {
        try
        {
            return FindContentGridIn(NavView);
        }
        catch { return null; }
    }

    private Microsoft.UI.Xaml.Controls.Grid FindContentGridIn(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Microsoft.UI.Xaml.Controls.SplitView sv)
            {
                var g = FindGridNamed(sv.Content as DependencyObject, "ContentGrid");
                if (g != null) return g;
            }
            var deep = FindContentGridIn(child);
            if (deep != null) return deep;
        }
        return null;
    }

    private Microsoft.UI.Xaml.Controls.Grid FindGridNamed(DependencyObject parent, string name)
    {
        if (parent == null) return null;
        if (parent is Microsoft.UI.Xaml.Controls.Grid g && g.Name == name) return g;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var result = FindGridNamed(child, name);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>应用主题（深/浅色）与外观设置，即时生效无需重启</summary>
    private void ApplyTheme()
    {
        // 1. 主题（ThemeResource 引用处即时更新）
        try
        {
            var root = Content as FrameworkElement;
            if (root != null)
                root.RequestedTheme = _settings.DarkMode ? ElementTheme.Dark : ElementTheme.Light;
        }
        catch { }

        // 2. 毛玻璃面板颜色（代码硬编码，需手动刷新）
        ApplyGlassEffects();

        // 3. 外观设置（主题色/亚克力）
        ApplyAppearance();
    }

    /// <summary>应用外观设置：主题色 + 亚克力（SPW 风格可选模式，默认关闭保留原界面）</summary>
    private void ApplyAppearance()
    {
        // 1. 主题色资源（ThemeResource 引用处即时更新）
        try
        {
            var c = _settings.GetAccentColor();
            Application.Current.Resources["AppAccentBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(c);
        }
        catch { }

        // 2. 背景 + 亚克力
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 2a. 自定义背景（纯色/图片）优先于主题默认
            if (_settings.BackgroundMode == 1)
            {
                // 纯色背景
                var c = ParseColor(_settings.BackgroundColor);
                if (Content is Grid root)
                    root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(c);
                ApplyTranslucentSurfaces();   // 内容区半透明让背景透出
                AcrylicHelper.Disable(hwnd);
                return;
            }
            if (_settings.BackgroundMode == 2 && !string.IsNullOrEmpty(_settings.BackgroundImagePath)
                && System.IO.File.Exists(_settings.BackgroundImagePath))
            {
                // 图片背景
                var imgBrush = new Microsoft.UI.Xaml.Media.ImageBrush
                {
                    ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_settings.BackgroundImagePath)),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                };
                if (Content is Grid root)
                    root.Background = imgBrush;
                ApplyTranslucentSurfaces();
                AcrylicHelper.Disable(hwnd);
                return;
            }

            // 2b. 亚克力（SPW 同款 SystemBackdrop，非打包模式已验证可用）
            if (_settings.AcrylicEnabled)
            {
                var ok = false;
                try
                {
                    // 官方亚克力：tint 跟随系统深浅主题自动切换（WinAppSDK 1.8）
                    SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                    ok = true;
                }
                catch { }

                if (!ok)
                {
                    // 回退：Win32 SetWindowCompositionAttribute（tint 跟随深浅模式）
                    var acrylicTint = _settings.DarkMode
                        ? Windows.UI.Color.FromArgb(255, 24, 24, 26)
                        : Windows.UI.Color.FromArgb(255, 240, 240, 242);
                    AcrylicHelper.Enable(hwnd, acrylicTint, _settings.AcrylicOpacity);
                }

                if (Content is Grid root)
                {
                    // 半透明基底色（深/浅），透过它看到窗口背后的图层
                    var a = (byte)Math.Clamp((int)(_settings.AcrylicOpacity * 255), 15, 230);
                    var baseColor = _settings.DarkMode
                        ? Windows.UI.Color.FromArgb(a, 24, 24, 26)      // 深色基底
                        : Windows.UI.Color.FromArgb(a, 247, 247, 248);  // 浅色基底
                    root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(baseColor);

                    // 内容区（ContentGrid）直接透明化：属性级赋值，切主题即时生效，
                    // 绕开模板 Setter 静态解析（默认 80% 不透明会把亚克力挡成实心）
                    var contentGrid = FindNavContentGrid();
                    if (contentGrid != null)
                        contentGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
                    // 阴影接收层同步半透明（与内容区一致，避免挡住亚克力）
                    if (PhotoShadowReceiver != null)
                        PhotoShadowReceiver.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));

                    // 侧边栏：保持模板默认（浅色 Pane 自带白底，
                    // 深色 Pane 透明 → 透出半透明根背景 → 亚克力可见）。
                    // 绝不设置 NavView.Background —— 它会盖住整个控件区域，
                    // 把亚克力挡成实心。
                    NavView.ClearValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty);
                }
            }
            else
            {
                SystemBackdrop = null;   // 移除官方亚克力
                AcrylicHelper.Disable(hwnd);
                if (Content is Grid root)
                    root.ClearValue(Grid.BackgroundProperty);
                // 内容区恢复主题默认背景（跟随深浅色，属性级即时生效）
                var contentGrid = FindNavContentGrid();
                if (contentGrid != null)
                    contentGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_settings.DarkMode
                        ? Windows.UI.Color.FromArgb(255, 32, 32, 36)
                        : Windows.UI.Color.FromArgb(255, 252, 252, 253));
                // 阴影接收层恢复默认（ThemeResource 主题背景）
                if (PhotoShadowReceiver != null)
                    PhotoShadowReceiver.ClearValue(Border.BackgroundProperty);
                NavView.ClearValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty);
            }
        }
        catch { }
    }

    // ══════════ 分类 ══════════

    /// <summary>预览入场：渐入动画（安全 — 仅 Opacity，不触及 RenderTransform）</summary>
    private void AnimatePreviewIn()
    {
        PreviewContentRoot.Opacity = 0.5;
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0.5, To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fade, PreviewContentRoot);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>预览退场：渐隐并关闭</summary>
    private void AnimatePreviewOut()
    {
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 1.0, To = 0.0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fade, PreviewContentRoot);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            PreviewOverlay.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            _previewIndex = -1;
            PreviewContentRoot.Opacity = 1.0;
        };
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
        RebuildTimelineNavItems();
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

    /// <summary>重建导航栏中的时间线子项（按年月分组）</summary>
    private void RebuildTimelineNavItems()
    {
        var tlParent = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(nvi => nvi.Content?.ToString() == "时间线");
        if (tlParent == null) return;

        tlParent.MenuItems.Clear();

        var groups = _allPhotos
            .GroupBy(p => p.TimelineDate.ToString("yyyy-MM"))
            .OrderByDescending(g => g.Key);

        foreach (var g in groups)
        {
            var month = g.Key;
            var item = new NavigationViewItem
            {
                Content = $"{month}（{g.Count()}）",
                Tag = "T:" + month,
            };
            item.IsSelected = _timelineFilter == month;
            tlParent.MenuItems.Add(item);
        }
    }

    /// <summary>管理分类对话框 —— 新建/重命名/删除</summary>
    private async void ManageCategories_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };

        // 入场动画
        panel.Opacity = 0;
        panel.RenderTransform = new CompositeTransform { TranslateY = 16 };
        panel.Loaded += (_, _) =>
        {
            var sb = new Storyboard();
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(250), EnableDependentAnimation = true };
            Storyboard.SetTarget(fade, panel);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            var slide = new DoubleAnimation { From = 16, To = 0, Duration = TimeSpan.FromMilliseconds(280), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
            Storyboard.SetTarget(slide, panel);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            sb.Children.Add(slide);
            sb.Begin();
        };

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
        if (!string.IsNullOrEmpty(_timelineFilter))
            q = q.Where(p => p.TimelineDate.ToString("yyyy-MM") == _timelineFilter);
        if (_minRatingFilter > 0)
            q = q.Where(p => p.Rating >= _minRatingFilter);
        if (_dateFilterDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-_dateFilterDays);
            q = q.Where(p => p.DateAdded >= cutoff);
        }
        if (!string.IsNullOrWhiteSpace(_searchKeyword))
            q = q.Where(p => p.Filename.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase)
                          || p.Category.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase));

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
        {
            p.IsSelected = _selectedPaths.Contains(p.FilePath);
            _photos.Add(p);
        }

        PageInfo.Text = $"第 {_currentPage + 1}/{maxPage + 1} 页 · 共 {_currentView.Count} 张";
        PrevPageBtn.IsEnabled = _currentPage > 0;
        NextPageBtn.IsEnabled = _currentPage < maxPage;
        UpdateStats();
        LoadThumbnailsForCurrentPage();
    }

    private void UpdateStats()
    {
        var fav = _allPhotos.Count(p => p.IsFavorite);
        var sel = _selectedPaths.Count;
        StatsText.Text = sel > 0
            ? $"{_allPhotos.Count} 张照片 · {fav} 收藏 · 已选 {sel} 张"
            : $"{_allPhotos.Count} 张照片 · {fav} 收藏";
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
                _timelineFilter = "";
                FavOnlyCheckBox.IsChecked = false;
            }
            else if (tag == "Favorites")
            {
                _favoritesOnly = true;
                _categoryFilter = "";
                _timelineFilter = "";
                FavOnlyCheckBox.IsChecked = true;
            }
            else if (tag == "Timeline")
            {
                _timelineFilter = "";
                _categoryFilter = "";
                _favoritesOnly = false;
                FavOnlyCheckBox.IsChecked = false;
            }
            else if (tag.StartsWith("T:"))
            {
                _timelineFilter = tag[2..];
                _categoryFilter = "";
                _favoritesOnly = false;
                FavOnlyCheckBox.IsChecked = false;
            }
            else if (tag == "CatAll")
            {
                _categoryFilter = "";
                _timelineFilter = "";
                _favoritesOnly = false;
                FavOnlyCheckBox.IsChecked = false;
            }
            else if (tag.StartsWith("Cat:"))
            {
                _categoryFilter = tag[4..];
                _timelineFilter = "";
                _favoritesOnly = false;
                FavOnlyCheckBox.IsChecked = false;
            }
            else return;

            _currentPage = 0;
            RefreshPhotos();
            UpdateCategoryButtons();
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
        _sortBy = SortCombo.SelectedIndex;
        RefreshPhotos();
    }

    private void FilterCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _minRatingFilter = FilterRatingCombo.SelectedIndex;         // 0=不限, 1..5
        _dateFilterDays = FilterDateCombo.SelectedIndex switch { 1 => 7, 2 => 30, 3 => 90, _ => 0 };
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

        // 入场动画
        panel.Opacity = 0;
        panel.RenderTransform = new CompositeTransform { TranslateY = 12 };
        panel.Loaded += (_, _) =>
        {
            var sb = new Storyboard();
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true };
            Storyboard.SetTarget(fade, panel); Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            var slide = new DoubleAnimation { From = 12, To = 0, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
            Storyboard.SetTarget(slide, panel); Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            sb.Children.Add(slide);
            sb.Begin();
        };

        var themeLabel = new TextBlock { Text = "外观主题", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 };
        var darkToggle = new ToggleSwitch { Header = "深色模式", IsOn = _settings.DarkMode, OnContent = "🌙 深色", OffContent = "☀️ 浅色" };

        // ── SPW 风格外观（可选，默认关闭保留原界面）──
        var lookLabel = new TextBlock { Text = "外观风格（SPW 风格 · 可选）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) };

        var acrylicToggle = new ToggleSwitch { Header = "亚克力毛玻璃", IsOn = _settings.AcrylicEnabled, OnContent = "已开启", OffContent = "已关闭" };
        var acrylicSlider = new Slider { Header = "亚克力透明度", Minimum = 0.05, Maximum = 0.95, StepFrequency = 0.05, Value = _settings.AcrylicOpacity, IsEnabled = _settings.AcrylicEnabled };
        acrylicToggle.Toggled += (_, _) => acrylicSlider.IsEnabled = acrylicToggle.IsOn;

        // 主题色：预设色板 + ColorPicker
        var accentRow = new StackPanel { Spacing = 6 };
        accentRow.Children.Add(new TextBlock { Text = "主题色", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var presetPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        // 主题预设（低饱和度色系）
        var presets = new (string Name, string Hex)[]
        {
            ("晴空蓝", "#5B6EAE"), ("湖水青", "#5B9EAE"), ("薄荷绿", "#5B9C6B"),
            ("暖沙橙", "#AE8A5B"), ("樱粉", "#AE6B9C"), ("黛紫", "#8A6BAE"), ("绯红", "#C45B5B"),
        };
        var colorPicker = new ColorPicker { Color = _settings.GetAccentColor(), IsAlphaEnabled = false, IsColorChannelTextInputVisible = true, IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring };
        foreach (var (name, hex) in presets)
        {
            var c = ParseColor(hex);
            var swatch = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0),
                Background = new SolidColorBrush(c),
                CornerRadius = new CornerRadius(7),
                Tag = hex,
            };
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) => colorPicker.Color = ParseColor(hex);
            presetPanel.Children.Add(swatch);
        }
        accentRow.Children.Add(presetPanel);
        accentRow.Children.Add(colorPicker);

        // ── 背景设置 ──
        var bgLabel = new TextBlock { Text = "背景", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) };
        var bgModeCombo = new ComboBox { Header = "背景模式", SelectedIndex = Math.Clamp(_settings.BackgroundMode, 0, 2), MinWidth = 160 };
        bgModeCombo.Items.Add(new ComboBoxItem { Content = "默认（跟随主题）" });
        bgModeCombo.Items.Add(new ComboBoxItem { Content = "纯色背景" });
        bgModeCombo.Items.Add(new ComboBoxItem { Content = "图片背景" });

        var bgColorPicker = new ColorPicker
        {
            Color = ParseColor(_settings.BackgroundColor),
            IsAlphaEnabled = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            Visibility = _settings.BackgroundMode == 1 ? Visibility.Visible : Visibility.Collapsed,
        };

        var bgImageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = _settings.BackgroundMode == 2 ? Visibility.Visible : Visibility.Collapsed };
        var bgPickBtn = new Button { Content = "选择图片…", Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), FontSize = 12 };
        var bgImageName = new TextBlock
        {
            Text = string.IsNullOrEmpty(_settings.BackgroundImagePath) ? "未选择" : System.IO.Path.GetFileName(_settings.BackgroundImagePath),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var bgClearBtn = new Button { Content = "清除", Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), FontSize = 12, Visibility = string.IsNullOrEmpty(_settings.BackgroundImagePath) ? Visibility.Collapsed : Visibility.Visible };
        bgImageRow.Children.Add(bgPickBtn);
        bgImageRow.Children.Add(bgImageName);
        bgImageRow.Children.Add(bgClearBtn);

        bgModeCombo.SelectionChanged += (_, _) =>
        {
            var mode = bgModeCombo.SelectedIndex;
            bgColorPicker.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            bgImageRow.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        };
        bgPickBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp"); picker.FileTypeFilter.Add(".gif");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _settings.BackgroundImagePath = file.Path;
                _settings.Save();
                bgImageName.Text = file.Name;
                bgClearBtn.Visibility = Visibility.Visible;
                ApplyAppearance();
            }
        };
        bgClearBtn.Click += (_, _) =>
        {
            _settings.BackgroundImagePath = "";
            _settings.Save();
            bgImageName.Text = "未选择";
            bgClearBtn.Visibility = Visibility.Collapsed;
            ApplyAppearance();
        };

        // 圆角
        var radiusCombo = new ComboBox { Header = "卡片圆角", SelectedIndex = RadiusIndex(_settings.CardCornerRadius), MinWidth = 140 };
        radiusCombo.Items.Add(new ComboBoxItem { Content = "小圆角 (8)" });
        radiusCombo.Items.Add(new ComboBoxItem { Content = "圆角 (14)" });
        radiusCombo.Items.Add(new ComboBoxItem { Content = "大圆角 (20)" });

        // 动画
        var animToggle = new ToggleSwitch { Header = "入场动画", IsOn = _settings.AnimationsEnabled, OnContent = "已开启", OffContent = "已关闭" };
        var animSlider = new Slider { Header = "动画时长（毫秒）", Minimum = 150, Maximum = 600, StepFrequency = 25, Value = _settings.AnimationDurationMs, IsEnabled = _settings.AnimationsEnabled };
        animToggle.Toggled += (_, _) => animSlider.IsEnabled = animToggle.IsOn;

        var autoLabel = new TextBlock { Text = "自动扫描", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) };
        var autoToggle = new ToggleSwitch { Header = "自动检测新增图片", IsOn = _settings.AutoScan, OnContent = "已开启", OffContent = "已关闭" };
        var intervalBox = new NumberBox { Header = "扫描间隔（分钟）", Value = _settings.AutoScanIntervalMinutes, Minimum = 1, Maximum = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, IsEnabled = _settings.AutoScan };
        autoToggle.Toggled += (_, _) => { intervalBox.IsEnabled = autoToggle.IsOn; };

        var watchLabel = new TextBlock { Text = $"已监控 {_settings.WatchedFolders.Count} 个文件夹", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };

        panel.Children.Add(themeLabel);
        panel.Children.Add(darkToggle);
        panel.Children.Add(lookLabel);
        panel.Children.Add(acrylicToggle);
        panel.Children.Add(acrylicSlider);
        panel.Children.Add(accentRow);
        panel.Children.Add(radiusCombo);
        panel.Children.Add(animToggle);
        panel.Children.Add(animSlider);
        panel.Children.Add(bgLabel);
        panel.Children.Add(bgModeCombo);
        panel.Children.Add(bgColorPicker);
        panel.Children.Add(bgImageRow);
        panel.Children.Add(autoLabel);
        panel.Children.Add(autoToggle);
        panel.Children.Add(intervalBox);
        panel.Children.Add(watchLabel);

        // 面板包 ScrollViewer：内容超出 ContentDialog 高度时可滚动
        // （背景设置等选项在窗口较矮时也能完整看到）
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var dlg = new ContentDialog { Title = "⚙ 设置", Content = scroll, PrimaryButtonText = "保存", SecondaryButtonText = "恢复默认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };

        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            // ── 一键恢复默认设置 ──
            var rd = new ContentDialog
            {
                Title = "恢复默认设置",
                Content = "将恢复所有设置为默认值（主题、外观、自动扫描）。",
                PrimaryButtonText = "恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await rd.ShowAsync() == ContentDialogResult.Primary)
            {
                _settings.DarkMode = false;
                _settings.AccentColor = "#5B6EAE";
                _settings.Saturation = 0.55;
                _settings.AcrylicEnabled = false;
                _settings.AcrylicOpacity = 0.55;
                _settings.CardCornerRadius = 14;
                _settings.AnimationsEnabled = true;
                _settings.AnimationDurationMs = 350;
                _settings.BackgroundMode = 0;
                _settings.BackgroundColor = "#2A2A32";
                _settings.BackgroundImagePath = "";
                _settings.AutoScan = true;
                _settings.AutoScanIntervalMinutes = 5;
                _settings.Save();
                ApplyTheme();
                RefreshPhotos();
                if (_settings.AutoScan) _folderWatcher.Start(_settings.AutoScanIntervalMinutes, _settings.WatchedFolders);
                else _folderWatcher.Stop();
                StatusText.Text = "已恢复默认设置";
            }
            return;
        }

        if (result == ContentDialogResult.Primary)
        {
            _settings.DarkMode = darkToggle.IsOn;
            _settings.AutoScan = autoToggle.IsOn;
            _settings.AutoScanIntervalMinutes = Math.Max(1, (int)intervalBox.Value);

            // ── 外观设置（SPW 风格）──
            _settings.AcrylicEnabled = acrylicToggle.IsOn;
            _settings.AcrylicOpacity = acrylicSlider.Value;
            _settings.AccentColor = ColorToHex(colorPicker.Color);
            _settings.CardCornerRadius = radiusCombo.SelectedIndex switch { 0 => 8.0, 2 => 20.0, _ => 14.0 };
            _settings.AnimationsEnabled = animToggle.IsOn;
            _settings.AnimationDurationMs = animSlider.Value;

            // ── 背景 ──
            _settings.BackgroundMode = bgModeCombo.SelectedIndex;
            _settings.BackgroundColor = ColorToHex(bgColorPicker.Color);
            _settings.Save();

            // 即时应用全部外观（深/浅色切换无需重启）
            ApplyTheme();
            if (radiusCombo.SelectedIndex != RadiusIndex(_settings.CardCornerRadius) || !_settings.AnimationsEnabled)
                RefreshPhotos();

            if (_settings.AutoScan) _folderWatcher.Start(_settings.AutoScanIntervalMinutes, _settings.WatchedFolders);
            else _folderWatcher.Stop();
            StatusText.Text = "设置已保存";
        }
    }

    // ══════════ 照片卡片 ══════════

    private static Windows.UI.Color ParseColor(string hex)
    {
        try
        {
            var h = hex.TrimStart('#');
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
        }
        catch { return Windows.UI.Color.FromArgb(255, 91, 110, 174); }
    }

    private static string ColorToHex(Windows.UI.Color c)
        => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static int RadiusIndex(double r)
        => r <= 8 ? 0 : r >= 20 ? 2 : 1;

    private void PhotoCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItem photo)
            OpenPreview(photo);
    }

    private void PhotoCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItem photo) return;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "🖼 打开预览", Tag = "open" });

        var fav = new MenuFlyoutItem { Text = photo.IsFavorite ? "☆ 取消收藏" : "★ 收藏", Tag = "fav" };
        menu.Items.Add(fav);

        // 分类子菜单
        var catMenu = new MenuFlyoutSubItem { Text = "🏷 分类" };
        catMenu.Items.Add(new MenuFlyoutItem { Text = "（未分类）", Tag = "cat:" });
        foreach (var c in _categories)
            catMenu.Items.Add(new MenuFlyoutItem { Text = c, Tag = "cat:" + c });
        menu.Items.Add(catMenu);

        // 评分子菜单
        var ratingMenu = new MenuFlyoutSubItem { Text = "⭐ 评分" };
        for (int i = 1; i <= 5; i++)
            ratingMenu.Items.Add(new MenuFlyoutItem { Text = i + " 星" + (photo.Rating == i ? " ✓" : ""), Tag = "rate:" + i });
        menu.Items.Add(ratingMenu);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "📂 打开所在文件夹", Tag = "folder" });
        menu.Items.Add(new MenuFlyoutItem { Text = "🗑 从图库移除", Tag = "remove" });

        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            item.Click += (s, args) => ContextMenuAction(photo, (s as MenuFlyoutItem)?.Tag as string ?? "");
        foreach (var sub in menu.Items.OfType<MenuFlyoutSubItem>())
            foreach (var item in sub.Items.OfType<MenuFlyoutItem>())
                item.Click += (s, args) => ContextMenuAction(photo, (s as MenuFlyoutItem)?.Tag as string ?? "");

        menu.ShowAt(sender as FrameworkElement, e.GetPosition(sender as FrameworkElement));
        e.Handled = true;
    }

    private async void ContextMenuAction(PhotoItem photo, string action)
    {
        switch (action)
        {
            case "open": OpenPreview(photo); break;
            case "fav":
                photo.IsFavorite = !photo.IsFavorite;
                ScheduleSave(); UpdateStats();
                if (_favoritesOnly && !photo.IsFavorite) RefreshPhotos();
                break;
            case "folder":
                try { Process.Start("explorer.exe", $"/select,\"{photo.FilePath}\""); }
                catch { StatusText.Text = "无法打开文件夹"; }
                break;
            case "remove": await RemoveFromLibraryAsync(photo); break;
            default:
                if (action.StartsWith("cat:"))
                {
                    var newCat = action[4..];
                    if (newCat == "（未分类）") newCat = "";
                    photo.Category = newCat;
                    RebuildCategories(); RefreshPhotos(); ScheduleSave();
                }
                else if (action.StartsWith("rate:"))
                {
                    photo.Rating = int.Parse(action[5..]);
                    ScheduleSave(); RefreshPhotos();
                }
                break;
        }
    }

    private void PhotoGrid_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        // 卡片入场：渐入动画（可配置时长/开关）
        if (args.Element is UIElement element)
        {
            if (_settings.AnimationsEnabled)
            {
                element.Opacity = 0;
                var sb = new Storyboard();
                var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(_settings.AnimationDurationMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
                Storyboard.SetTarget(fade, element); Storyboard.SetTargetProperty(fade, "Opacity");
                sb.Children.Add(fade);
                sb.Begin();
            }
            else
            {
                element.Opacity = 1;
            }

            // 卡片阴影（ThemeShadow）+ 悬停交互：无条件执行（不依赖圆角设置）
            // 模板根现在是 Grid：子 0 = 伪阴影层 CardShadowLayer，子 1 = 卡片 CardRoot
            if (element is Grid cardHost && cardHost.Children.Count >= 2
                && cardHost.Children[0] is Border shadowLayer && cardHost.Children[1] is Border card)
            {
                var dark = _settings.DarkMode;
                var acrylic = _settings.AcrylicEnabled;

                // 伪阴影层：深色=白色亮边（深色背景上看不到黑阴影），浅色=黑色投影
                var shadowColor = dark
                    ? Windows.UI.Color.FromArgb(45, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(35, 0, 0, 0);
                var shadowBrush = new SolidColorBrush(shadowColor);
                shadowLayer.Background = shadowBrush;

                try
                {
                    var shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
                    if (PhotoShadowReceiver != null)
                        shadow.Receivers.Add(PhotoShadowReceiver);
                    card.Shadow = shadow;
                }
                catch { /* 阴影不可用时不影响卡片 */ }

                // 自定义圆角（SPW 风格外观设置），同步伪阴影层
                if (_settings.CardCornerRadius > 0)
                {
                    var r = _settings.CardCornerRadius;
                    card.CornerRadius = new CornerRadius(r);
                    shadowLayer.CornerRadius = new CornerRadius(r);
                    // 同步内层图片容器圆角（避免溢出）
                    if (card.Child is Grid g && g.Children.Count > 0 && g.Children[0] is Border imgBorder)
                        imgBorder.CornerRadius = new CornerRadius(r, r, 0, 0);
                }

                // 卡片背景：亚克力模式 = 玻璃效果（半透明，透明度与整体一致，可透出背后）；
                // 普通模式 = 主题背景（XAML ThemeResource 自适应），hover 时微亮。
                // 所有颜色动态读取当前主题（快照会在主题切换后残留 → 信息区全白）
                void ApplyCardBackground(bool hover)
                {
                    var isDark = _settings.DarkMode;
                    if (acrylic)
                    {
                        // 玻璃卡片：比整体透明度略实一点（+35），保证文字可读
                        var a = (byte)Math.Clamp((int)(_settings.AcrylicOpacity * 255) + 35, 90, 240);
                        var c = isDark ? (byte)26 : (byte)250;
                        var alpha = hover ? (byte)Math.Min(a + 25, 255) : a;
                        card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, c, c, isDark ? (byte)30 : (byte)252));
                        shadowLayer.Background = new SolidColorBrush(hover
                            ? Windows.UI.Color.FromArgb(70, 255, 255, 255)
                            : Windows.UI.Color.FromArgb(45, 255, 255, 255));
                    }
                    else
                    {
                        if (hover)
                            card.Background = new SolidColorBrush(isDark
                                ? Windows.UI.Color.FromArgb(255, 58, 58, 64)
                                : Windows.UI.Color.FromArgb(255, 255, 255, 255));
                        else
                            card.ClearValue(Border.BackgroundProperty);   // 恢复 ThemeResource
                        shadowLayer.Background = hover
                            ? new SolidColorBrush(isDark
                                ? Windows.UI.Color.FromArgb(70, 255, 255, 255)
                                : Windows.UI.Color.FromArgb(60, 0, 0, 0))
                            : shadowBrush;
                    }
                }

                ApplyCardBackground(false);
                card.PointerEntered += (_, _) => ApplyCardBackground(true);
                card.PointerExited += (_, _) => ApplyCardBackground(false);
            }
        }
    }

    private void PhotoCard_DragStarting(object sender, DragStartingEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItem photo)
        {
            try
            {
                var file = Windows.Storage.StorageFile.GetFileFromPathAsync(photo.FilePath).GetAwaiter().GetResult();
                e.Data.SetStorageItems(new[] { file });
                e.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            }
            catch
            {
                e.Cancel = true;
            }
        }
        else
        {
            e.Cancel = true;
        }
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

    private void SelectToggle_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 阻止冒泡到卡片 Border 的 Tapped（防止打开预览）
        e.Handled = true;
    }

    private void SelectToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var path = btn.Tag as string ?? "";
        if (string.IsNullOrEmpty(path)) return;

        var photo = _allPhotos.FirstOrDefault(p => p.FilePath == path);
        if (photo == null) return;

        // 取消分类模式下只允许选已分类照片
        if (_cancelCatMode && string.IsNullOrEmpty(photo.Category))
        {
            StatusText.Text = "此照片未分类，无法移出";
            return;
        }

        if (_selectedPaths.Contains(path))
        {
            _selectedPaths.Remove(path);
            photo.IsSelected = false;
        }
        else
        {
            _selectedPaths.Add(path);
            photo.IsSelected = true;
        }

        UpdateStats();
    }

    private void SelectAllPage_Click(object s, RoutedEventArgs e)
    {
        if (!_batchMode) return;
        var pagePhotos = _currentView.Skip(_currentPage * PageSize).Take(PageSize).ToList();

        // 筛选可选照片
        var selectable = _cancelCatMode
            ? pagePhotos.Where(p => !string.IsNullOrEmpty(p.Category)).ToList()
            : pagePhotos;

        // 如果当前页所有可选照片已全选 → 取消全选
        var allSelected = selectable.Count > 0 && selectable.All(p => _selectedPaths.Contains(p.FilePath));

        foreach (var p in selectable)
        {
            if (allSelected)
            {
                _selectedPaths.Remove(p.FilePath);
                p.IsSelected = false;
            }
            else
            {
                _selectedPaths.Add(p.FilePath);
                p.IsSelected = true;
            }
        }
        UpdateStats();
    }

    private void EnterBatchMode()
    {
        _batchMode = true;
        _selectedPaths.Clear();
        foreach (var p in _allPhotos) p.SelectVisible = true;
        ExitBatchBtn.Visibility = Visibility.Visible;
        SelectAllPageBtn.Visibility = Visibility.Visible;
        StatusText.Text = "多选模式：点击卡片左上角 ☐ 选择照片";
    }

    private void ExitBatchMode()
    {
        _batchMode = false;
        _cancelCatMode = false;
        _selectedPaths.Clear();
        foreach (var p in _allPhotos) p.SelectVisible = false;
        ExitBatchBtn.Visibility = Visibility.Collapsed;
        SelectAllPageBtn.Visibility = Visibility.Collapsed;
        RemoveFromCatBtn.Visibility = Visibility.Collapsed;
        DissolveCatBtn.Visibility = Visibility.Collapsed;
        UpdateStats();
    }

    private void ExitBatch_Click(object s, RoutedEventArgs e) => ExitBatchMode();

    private void UpdateCategoryButtons()
    {
        // 当前正在查看某个分类时，显示「移出分类」和「解散分类」按钮
        var isCatView = !string.IsNullOrEmpty(_categoryFilter);
        RemoveFromCatBtn.Visibility = isCatView && _batchMode ? Visibility.Visible : Visibility.Collapsed;
        DissolveCatBtn.Visibility = isCatView && _batchMode ? Visibility.Visible : Visibility.Collapsed;
    }

    // ══════════ 预览 ══════════

    private void OpenPreview(PhotoItem photo)
    {
        var idx = _currentView.IndexOf(photo);
        if (idx < 0) return;
        PreviewOverlay.Visibility = Visibility.Visible;
        ShowPreviewAt(idx);
        AnimatePreviewIn();
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
        PreviewExif.Text = "读取中…";
        _ = LoadExifAsync(p.FilePath);

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

    private int _exifRequestSeq;
    private async Task LoadExifAsync(string filePath)
    {
        var seq = ++_exifRequestSeq;
        var text = await ExifReader.LoadExifTextAsync(filePath);
        if (seq != _exifRequestSeq) return; // 已切换到其他照片，丢弃过期结果
        PreviewExif.Text = text ?? "（无 EXIF 信息）";
    }

    private void ClosePreview()
    {
        AnimatePreviewOut();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 焦点在输入框时不拦截按键（搜索/分类输入）
        if (FocusManager.GetFocusedElement() is TextBox or PasswordBox)
            return;

        // ── 预览模式下 ──
        if (PreviewOverlay.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case VirtualKey.Escape: ClosePreview(); e.Handled = true; break;
                case VirtualKey.Left: if (PreviewPrevBtn.IsEnabled) { ShowPreviewAt(_previewIndex - 1); e.Handled = true; } break;
                case VirtualKey.Right: if (PreviewNextBtn.IsEnabled) { ShowPreviewAt(_previewIndex + 1); e.Handled = true; } break;
                case VirtualKey.Delete:
                    if (_previewIndex >= 0 && _previewIndex < _currentView.Count)
                    {
                        RemoveFromLibraryAsync(_currentView[_previewIndex]);
                        e.Handled = true;
                    }
                    break;
            }
            return;
        }

        // ── 主界面快捷键 ──
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                   .HasFlag(CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Left:
                if (PrevPageBtn.IsEnabled) { PrevPage_Click(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case VirtualKey.Right:
                if (NextPageBtn.IsEnabled) { NextPage_Click(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case VirtualKey.F5:
                RefreshPhotos(); StatusText.Text = "已刷新"; e.Handled = true;
                break;
            case VirtualKey.Space:
                if (_photos.Count > 0) OpenPreview(_photos[0]); e.Handled = true;
                break;
            case VirtualKey.A when ctrl:
                if (_batchMode) { SelectAllPage_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else { EnterBatchMode(); SelectAllPage_Click(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case VirtualKey.Delete:
                if (_selectedPaths.Count > 0) { RemoveSelectedAsync(); e.Handled = true; }
                break;
            case VirtualKey.Escape:
                if (_batchMode) { ExitBatchMode(); e.Handled = true; }
                break;
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
        await RemoveFromLibraryAsync(_currentView[_previewIndex]);
    }

    /// <summary>从图库移除（仅移除图库记录，绝不删除磁盘文件），带确认对话框。</summary>
    private async Task RemoveFromLibraryAsync(PhotoItem p)
    {
        var dlg = new ContentDialog { Title = "从图库移除", Content = $"将「{p.Filename}」从图库移除？\n磁盘上的文件不会被删除。", PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _allPhotos.Remove(p);
        RefreshPhotos();
        ScheduleSave();
        StatusText.Text = $"已移除 {p.Filename}（文件仍在磁盘上）";
        ShowPreviewAt(_previewIndex);
    }

    /// <summary>批量移除选中的照片（仅图库记录，不删磁盘文件）。</summary>
    private async Task RemoveSelectedAsync()
    {
        var targets = _allPhotos.Where(p => _selectedPaths.Contains(p.FilePath)).ToList();
        if (targets.Count == 0) return;

        var dlg = new ContentDialog { Title = "批量移除", Content = $"将选中的 {targets.Count} 张照片从图库移除？\n磁盘上的文件不会被删除。", PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var p in targets)
        {
            _allPhotos.Remove(p);
            _selectedPaths.Remove(p.FilePath);
        }
        ExitBatchMode();
        RefreshPhotos();
        ScheduleSave();
        StatusText.Text = $"已移除 {targets.Count} 张（文件仍在磁盘上）";
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

    private async void DuplicateCheck_Click(object s, RoutedEventArgs e)
    {
        if (_allPhotos.Count < 2) { StatusText.Text = "照片太少，无需查重"; return; }

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在计算文件哈希…";
        var progress = new Progress<int>(n => StatusText.Text = $"正在检查重复… {n} 个文件");
        var groups = await DuplicateScanner.FindDuplicatesAsync(_allPhotos, progress);
        BusyRing.Visibility = Visibility.Collapsed;

        if (groups.Count == 0)
        {
            StatusText.Text = "✅ 未发现重复照片";
            return;
        }

        // 构建结果对话框
        var panel = new StackPanel { Spacing = 10 };
        var totalDupes = groups.Sum(g => g.Count - 1);
        panel.Children.Add(new TextBlock
        {
            Text = $"发现 {groups.Count} 组重复（共 {totalDupes} 张冗余照片）。移除仅从图库删除记录，不会删除磁盘文件。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var groupIdx = 0;
        foreach (var group in groups)
        {
            groupIdx++;
            var keep = group[0];
            var box = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
            };
            var inner = new StackPanel { Spacing = 6 };

            var header = new TextBlock
            {
                Text = $"组 {groupIdx} · {group.Count} 张 · {keep.SizeText}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
            };
            inner.Children.Add(header);

            foreach (var p in group)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = p.Filename,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                if (p != keep)
                {
                    var rmBtn = new Button
                    {
                        Content = "移除",
                        FontSize = 11,
                        Padding = new Thickness(8, 3, 8, 3),
                        CornerRadius = new CornerRadius(4),
                        Tag = p,
                    };
                    rmBtn.Click += DupeRemove_Click;
                    Grid.SetColumn(rmBtn, 1);
                    row.Children.Add(rmBtn);
                }
                else
                {
                    var keepTag = new TextBlock { Text = "保留", FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SoftGreenBrush"], VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(keepTag, 1);
                    row.Children.Add(keepTag);
                }
                inner.Children.Add(row);
            }
            box.Child = inner;
            panel.Children.Add(box);
        }

        var scroll = new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dlg = new ContentDialog
        {
            Title = "🔄 重复照片",
            Content = scroll,
            PrimaryButtonText = "移除全部重复",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var removed = 0;
            foreach (var group in groups)
                for (var i = 1; i < group.Count; i++)
                    if (_allPhotos.Remove(group[i])) removed++;
            RefreshPhotos();
            ScheduleSave();
            StatusText.Text = $"已移除 {removed} 张重复照片（磁盘文件保留）";
        }
    }

    private void DupeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PhotoItem p) return;
        _allPhotos.Remove(p);
        RefreshPhotos();
        ScheduleSave();
        StatusText.Text = $"已移除 {p.Filename}（磁盘文件保留）";
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

    private async void BatchClassify_Click(object s, RoutedEventArgs e)
    {
        if (!_batchMode) { EnterBatchMode(); return; }

        // 仅对选中的照片操作
        if (_selectedPaths.Count == 0) { StatusText.Text = "请先选择照片"; return; }
        var targets = _allPhotos.Where(p => _selectedPaths.Contains(p.FilePath)).ToList();

        var scopeText = $"已选 {targets.Count} 张";

        // 构建分类选择对话框
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = $"将为 {scopeText} 照片设置分类：", FontSize = 13 });
        var combo = new ComboBox { MinWidth = 200, PlaceholderText = "选择分类" };
        foreach (var cat in _categories) combo.Items.Add(cat);
        if (_categories.Count > 0) combo.SelectedIndex = 0;
        panel.Children.Add(combo);
        var newBox = new TextBox { PlaceholderText = "或输入新分类名称…", MinWidth = 200 };
        panel.Children.Add(newBox);

        var dlg = new ContentDialog { Title = "🏷 批量分类", Content = panel, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };

        // 入场动画
        panel.Opacity = 0;
        panel.Loaded += (_, _) =>
        {
            var sb = new Storyboard();
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true };
            Storyboard.SetTarget(fade, panel); Storyboard.SetTargetProperty(fade, "Opacity"); sb.Children.Add(fade);
            sb.Begin();
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) { ExitBatchMode(); return; }

        var newName = newBox.Text?.Trim();
        if (string.IsNullOrEmpty(newName))
            newName = combo.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(newName)) { StatusText.Text = "未指定分类"; ExitBatchMode(); return; }

        if (!_categories.Contains(newName, StringComparer.OrdinalIgnoreCase))
            _categories.Add(newName);

        foreach (var p in targets) p.Category = newName;
        RebuildCategories();
        RefreshPhotos();
        ScheduleSave();
        ExitBatchMode();
        StatusText.Text = $"已将 {targets.Count} 张照片分类为「{newName}」";
    }

    private void BatchUncategorize_Click(object s, RoutedEventArgs e)
    {
        // 跳转到分类导航，让用户在分类视图中操作
        if (_categories.Count == 0) { StatusText.Text = "还没有任何分类"; return; }
        _cancelCatMode = true;
        EnterBatchMode();

        // 选中「分类」导航项
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi && nvi.Content?.ToString() == "分类")
            {
                NavView.SelectedItem = nvi;
                nvi.IsExpanded = true;
                break;
            }
        }
        StatusText.Text = "取消分类模式：点击左侧分类查看照片，选择后移出或解散";
    }

    private void RemoveFromCategory_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_categoryFilter)) return;
        if (_selectedPaths.Count == 0) { StatusText.Text = "请先选择要移出的照片"; return; }

        var targets = _allPhotos.Where(p => _selectedPaths.Contains(p.FilePath)).ToList();
        foreach (var p in targets) p.Category = "";
        ExitBatchMode();
        RefreshPhotos();
        ScheduleSave();
        StatusText.Text = $"已将 {targets.Count} 张照片移出「{_categoryFilter}」";
    }

    private void DissolveCategory_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_categoryFilter)) return;
        var catName = _categoryFilter;
        var count = _allPhotos.Count(p => p.Category == catName);
        if (count == 0) return;

        foreach (var p in _allPhotos.Where(p => p.Category == catName)) p.Category = "";
        _categories.Remove(catName);
        _categoryFilter = "";
        ExitBatchMode();
        RebuildCategories();
        RefreshPhotos();
        ScheduleSave();
        StatusText.Text = $"已解散「{catName}」（{count} 张照片已取消分类）";
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

            var imported = new List<PhotoItem>();
            foreach (var f in found)
            {
                var p = new PhotoItem { Filename = Path.GetFileName(f.Path), FilePath = f.Path, FileSize = f.Size, DateAdded = DateTime.Now };
                _allPhotos.Add(p);
                imported.Add(p);
            }

            // 后台读取 EXIF 拍摄时间（不阻塞 UI）
            if (imported.Count > 0)
                _ = EnrichDateTakenAsync(imported);

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

    /// <summary>后台读取照片 EXIF 拍摄时间（DateTaken），完成后刷新时间线导航</summary>
    private async Task EnrichDateTakenAsync(List<PhotoItem> photos)
    {
        var results = new List<(PhotoItem P, DateTime? Date)>();
        await Task.Run(async () =>
        {
            foreach (var p in photos)
            {
                if (p.DateTaken.HasValue) continue;
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(p.FilePath);
                    var props = await file.Properties.GetImagePropertiesAsync();
                    var date = props.DateTaken != DateTimeOffset.MinValue ? props.DateTaken.LocalDateTime : (DateTime?)null;
                    results.Add((p, date));
                }
                catch { results.Add((p, null)); }
            }
        });
        foreach (var (p, d) in results)
            if (d.HasValue) p.DateTaken = d;
        ScheduleSave();
        RebuildTimelineNavItems();
    }

    /// <summary>启动后补读历史照片缺失的拍摄日期（后台逐张读取，不阻塞 UI）</summary>
    private async Task EnrichMissingDateTakenAsync()
    {
        var pending = _allPhotos.Where(p => !p.DateTaken.HasValue).ToList();
        if (pending.Count == 0) return;

        var done = 0;
        await Task.Run(async () =>
        {
            foreach (var p in pending)
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(p.FilePath);
                    var props = await file.Properties.GetImagePropertiesAsync();
                    if (props.DateTaken != DateTimeOffset.MinValue)
                        p.DateTaken = props.DateTaken.LocalDateTime;
                }
                catch { }
                done++;
                if (done % 100 == 0)
                {
                    var n = done;
                    DispatcherQueue.TryEnqueue(() => StatusText.Text = $"正在补读拍摄信息… {n}/{pending.Count}");
                }
            }
        });
        ScheduleSave();
        RebuildTimelineNavItems();
        StatusText.Text = $"拍摄信息读取完成（{pending.Count} 张）";
    }

    private void OnNewFilesFound(List<string> files)
    {
        var known = new HashSet<string>(_allPhotos.Select(p => p.FilePath), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var pending = new List<PhotoItem>();

        foreach (var path in files)
        {
            if (known.Contains(path)) continue;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) continue;
                var p = new PhotoItem { Filename = fi.Name, FilePath = fi.FullName, FileSize = fi.Length, DateAdded = DateTime.Now };
                _allPhotos.Add(p);
                pending.Add(p);
                added++;
            }
            catch { }
        }

        if (added > 0)
        {
            RebuildCategories();
            RefreshPhotos();
            ScheduleSave();
            if (pending.Count > 0)
                _ = EnrichDateTakenAsync(pending);
            StatusText.Text = $"🔍 自动发现 {added} 张新照片";
        }
    }
}
