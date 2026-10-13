using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Storage.Pickers;

namespace PhotoAlbum;

/// <summary>编辑独立草稿；只有主窗口确认保存后才提交到磁盘和运行中的应用。</summary>
public sealed class SettingsEditor : UserControl
{
    private sealed class Section
    {
        public string Name { get; }
        public StackPanel Panel { get; } = new() { Spacing = 10 };
        public List<(Border Card, string Keywords)> Cards { get; } = new();
        public Action Reset { get; set; } = () => { };
        public Section(string name) => Name = name;
    }

    public AppSettings Draft { get; }
    public event Action<ReleaseInfo>? UpdateRequested;
    private readonly List<Section> _sections = new();
    private readonly List<Action> _refreshers = new();
    private readonly ListView _navigation = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _search = new() { PlaceholderText = "搜索设置…" };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _notice = new() { Text = "调整后点击保存生效；取消会放弃本次修改。", FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { Text = "没有找到匹配的设置", Margin = new Thickness(0, 24, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _preview = new() { Padding = new Thickness(12), CornerRadius = new CornerRadius(12), MinHeight = 130 };
    private readonly ComboBox _profiles = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _removeProfile = new() { Content = "移除方案", FontSize = 12 };
    private readonly nint _owner;
    private readonly List<PhotoItem> _samplePhotos;
    private bool _refreshing;

    private static void ApplySecondary(TextBlock text)
    {
        void Refresh() => text.Foreground = new SolidColorBrush(text.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 190, 190, 198) : Windows.UI.Color.FromArgb(255, 85, 85, 95));
        text.Loaded += (_, _) => Refresh();
        text.ActualThemeChanged += (_, _) => Refresh();
        Refresh();
    }
    private static TextBlock Label(string text, int size = 12)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        ApplySecondary(label);
        return label;
    }

    public SettingsEditor(AppSettings settings, IEnumerable<PhotoItem> photos, nint owner, double width, double height)
    {
        Draft = settings.Clone();
        Draft.AppearanceProfiles ??= new();
        Draft.WatchedFolders ??= new();
        _samplePhotos = photos.Where(p => p.ThumbnailSource != null).Take(6).ToList();
        _owner = owner;
        Width = width;
        Height = height;
        var root = new Grid { ColumnSpacing = 20, RowSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sidebar = new StackPanel { Spacing = 12 };
        AutomationProperties.SetName(_search, "搜索设置");
        sidebar.Children.Add(_search);
        sidebar.Children.Add(_navigation);
        sidebar.Children.Add(Label("方案与修改均会在点击保存后生效。"));
        root.Children.Add(sidebar);
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(_scroll);
        ApplySecondary(_notice);
        Grid.SetRow(_notice, 1);
        Grid.SetColumnSpan(_notice, 2);
        root.Children.Add(_notice);
        Content = root;

        BuildAppearance();
        BuildGallery();
        BuildPreview();
        BuildFiles();
        BuildUpdates();
        var content = new StackPanel { Spacing = 16, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var section in _sections)
        {
            content.Children.Add(section.Panel);
            _navigation.Items.Add(section.Name);
        }
        content.Children.Add(_empty);
        _scroll.Content = content;
        _navigation.SelectionChanged += (_, _) => Filter();
        _search.TextChanged += (_, _) => Filter();
        _navigation.SelectedIndex = 0;
        RefreshControls();
    }

    private Section AddSection(string name)
    {
        var section = new Section(name);
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var reset = new Button { Content = "恢复本区默认", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        reset.Click += (_, _) => { section.Reset(); RefreshControls(); ShowNotice($"已暂存「{name}」默认值，点击保存生效。"); };
        Grid.SetColumn(reset, 1);
        heading.Children.Add(reset);
        section.Panel.Children.Add(heading);
        _sections.Add(section);
        return section;
    }

    private void AddSetting(Section section, string title, string description, FrameworkElement control)
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(description)) body.Children.Add(Label(description));
        AutomationProperties.SetName(control, title);
        body.Children.Add(control);
        var card = new Border
        {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1), Child = body,
        };
        void RefreshCard()
        {
            bool dark = card.ActualTheme == ElementTheme.Dark;
            card.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 48, 48, 53) : Colors.White);
            card.BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 72, 72, 80) : Windows.UI.Color.FromArgb(255, 218, 218, 224));
        }
        card.Loaded += (_, _) => RefreshCard();
        card.ActualThemeChanged += (_, _) => RefreshCard();
        RefreshCard();
        section.Panel.Children.Add(card);
        section.Cards.Add((card, title + " " + description));
    }

    private void Combo(Section section, string title, string description, string[] choices, Func<int> get, Action<int> set)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var choice in choices) box.Items.Add(choice);
        _refreshers.Add(() => box.SelectedIndex = Math.Clamp(get(), 0, choices.Length - 1));
        box.SelectionChanged += (_, _) => { if (!_refreshing && box.SelectedIndex >= 0) { set(box.SelectedIndex); Changed(section); } };
        AddSetting(section, title, description, box);
    }

    private void Toggle(Section section, string title, string description, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { OnContent = "开启", OffContent = "关闭" };
        _refreshers.Add(() => toggle.IsOn = get());
        toggle.Toggled += (_, _) => { if (!_refreshing) { set(toggle.IsOn); Changed(section); } };
        AddSetting(section, title, description, toggle);
    }

    private void Range(Section section, string title, string description, double min, double max, double step, Func<double> get, Action<double> set, string unit)
    {
        var panel = new StackPanel { Spacing = 2 };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step };
        AutomationProperties.SetName(slider, title);
        var value = Label("");
        void UpdateValue() => value.Text = $"{slider.Value:0.##} {unit}";
        _refreshers.Add(() => { slider.Value = Math.Clamp(get(), min, max); UpdateValue(); });
        slider.ValueChanged += (_, _) => { UpdateValue(); if (!_refreshing) { set(slider.Value); Changed(section); } };
        panel.Children.Add(slider);
        panel.Children.Add(value);
        AddSetting(section, title, description, panel);
    }

    private void BuildAppearance()
    {
        var s = AddSection("方案与外观");
        s.Reset = () =>
        {
            Draft.ThemeMode = 0; Draft.DarkMode = false; Draft.AccentColor = "#5B6EAE";
            Draft.AcrylicEnabled = false; Draft.AcrylicOpacity = .55; Draft.TopBarStyle = 0;
            Draft.BackgroundMode = 0; Draft.BackgroundColor = "#2A2A32"; Draft.BackgroundImagePath = "";
            Draft.ActiveAppearanceProfileId = "";
        };
        var scheme = new StackPanel { Spacing = 8 };
        var apply = new Button { Content = "套用方案", FontSize = 12 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(apply); row.Children.Add(_removeProfile);
        var name = new TextBox { PlaceholderText = "新方案名称（最多 30 字）", MaxLength = 30 };
        var save = new Button { Content = "保存当前配置为方案", FontSize = 12 };
        scheme.Children.Add(_profiles); scheme.Children.Add(row); scheme.Children.Add(name); scheme.Children.Add(save);
        _profiles.SelectionChanged += (_, _) => _removeProfile.IsEnabled = _profiles.SelectedItem is ComboBoxItem item && item.Tag is AppearanceProfile p && !p.Id.StartsWith("builtin-", StringComparison.Ordinal);
        apply.Click += (_, _) =>
        {
            if (_profiles.SelectedItem is not ComboBoxItem item || item.Tag is not AppearanceProfile profile) return;
            profile.ApplyTo(Draft); Draft.ActiveAppearanceProfileId = profile.Id;
            RefreshControls(); ShowNotice($"已套用「{profile.Name}」到草稿，点击保存生效。");
        };
        save.Click += (_, _) =>
        {
            var title = name.Text.Trim();
            if (title.Length == 0) { ShowNotice("请先输入方案名称。"); name.Focus(FocusState.Programmatic); return; }
            var old = Draft.AppearanceProfiles.FirstOrDefault(p => p.Name.Equals(title, StringComparison.OrdinalIgnoreCase));
            var profile = AppearanceProfile.Capture(Draft, title);
            if (old != null) { profile.Id = old.Id; Draft.AppearanceProfiles.Remove(old); }
            Draft.AppearanceProfiles.Add(profile); Draft.ActiveAppearanceProfileId = profile.Id;
            RefreshProfiles(profile.Id); name.Text = "";
            ShowNotice($"已暂存方案「{title}」，点击保存后保留。");
        };
        _removeProfile.Click += (_, _) =>
        {
            if (_profiles.SelectedItem is not ComboBoxItem item || item.Tag is not AppearanceProfile profile || profile.Id.StartsWith("builtin-", StringComparison.Ordinal)) return;
            Draft.AppearanceProfiles.Remove(profile);
            if (Draft.ActiveAppearanceProfileId == profile.Id) Draft.ActiveAppearanceProfileId = "";
            RefreshProfiles(""); ShowNotice("已从草稿移除方案，点击保存生效。");
        };
        AddSetting(s, "外观方案", "内置深色看图、浅色整理和沉浸展示；自定义方案同时保存图库布局与预览偏好。", scheme);
        AddSetting(s, "效果预览", "此处展示草稿的配色和布局，主界面在保存后更新。", _preview);
        Combo(s, "主题模式", "选择跟随系统、浅色或深色。", new[] { "跟随系统", "浅色", "深色" }, () => Draft.ThemeMode + 1, v => { Draft.ThemeMode = v - 1; Draft.DarkMode = v == 2; });
        AddColor(s, "主题色", () => Draft.GetAccentColor(), hex => Draft.AccentColor = hex, true);
        Toggle(s, "亚克力毛玻璃", "为侧栏和操作栏使用通透材质。", () => Draft.AcrylicEnabled, v => Draft.AcrylicEnabled = v);
        Range(s, "亚克力透明度", "开启毛玻璃后生效。", 0, 1, .05, () => Draft.AcrylicOpacity, v => Draft.AcrylicOpacity = v, "");
        Combo(s, "顶栏材质", "调整搜索工具栏的外观。", new[] { "磨砂", "液态玻璃" }, () => Draft.TopBarStyle, v => Draft.TopBarStyle = v);
        Combo(s, "背景模式", "默认主题、纯色或本地图片。", new[] { "跟随主题", "纯色", "图片" }, () => Draft.BackgroundMode, v => Draft.BackgroundMode = v);
        AddColor(s, "背景颜色", () => ParseColor(Draft.BackgroundColor), hex => Draft.BackgroundColor = hex, false);
        var imageRow = new StackPanel { Spacing = 8 };
        var filename = Label("");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pick = new Button { Content = "选择图片…", FontSize = 12 };
        var clear = new Button { Content = "清除选择", FontSize = 12 };
        buttons.Children.Add(pick); buttons.Children.Add(clear); imageRow.Children.Add(filename); imageRow.Children.Add(buttons);
        _refreshers.Add(() => { filename.Text = string.IsNullOrEmpty(Draft.BackgroundImagePath) ? "未选择图片" : Path.GetFileName(Draft.BackgroundImagePath); clear.IsEnabled = !string.IsNullOrEmpty(Draft.BackgroundImagePath); });
        pick.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            Draft.BackgroundImagePath = file.Path; Draft.BackgroundMode = 2;
            Changed(s); RefreshControls();
        };
        clear.Click += (_, _) => { Draft.BackgroundImagePath = ""; if (Draft.BackgroundMode == 2) Draft.BackgroundMode = 0; Changed(s); RefreshControls(); };
        AddSetting(s, "背景图片", "图片选择可随本次设置一起保存或取消。", imageRow);
    }

    private void AddColor(Section s, string title, Func<Windows.UI.Color> get, Action<string> set, bool presets)
    {
        var body = new StackPanel { Spacing = 8 };
        var picker = new ColorPicker { IsAlphaEnabled = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = true, Visibility = Visibility.Collapsed };
        var open = new Button { FontSize = 12, Content = "自定义颜色…" };
        _refreshers.Add(() => picker.Color = get());
        picker.ColorChanged += (_, _) =>
        {
            if (_refreshing) return;
            var c = picker.Color; set($"#{c.R:X2}{c.G:X2}{c.B:X2}"); Changed(s);
        };
        if (presets)
        {
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (name, hex) in new[] { ("晴空蓝", "#5B6EAE"), ("湖水青", "#5B9EAE"), ("薄荷绿", "#5B9C6B"), ("暖沙橙", "#AE8A5B"), ("樱粉", "#AE6B9C"), ("黛紫", "#8A6BAE") })
            {
                var swatch = new Button { Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(ParseColor(hex)) };
                ToolTipService.SetToolTip(swatch, name); AutomationProperties.SetName(swatch, name);
                swatch.Click += (_, _) => { set(hex); Changed(s); RefreshControls(); };
                swatches.Children.Add(swatch);
            }
            body.Children.Add(swatches);
        }
        open.Click += (_, _) => { picker.Visibility = picker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; open.Content = picker.Visibility == Visibility.Visible ? "收起取色器" : "自定义颜色…"; };
        body.Children.Add(open); body.Children.Add(picker);
        AddSetting(s, title, "", body);
    }

    private void BuildGallery()
    {
        var s = AddSection("图库布局");
        s.Reset = () => { Draft.DisplayMode = 0; Draft.CardMinWidth = 220; Draft.CardCornerRadius = 14; Draft.AnimationsEnabled = true; Draft.AnimationDurationMs = 350; Draft.ActiveAppearanceProfileId = ""; };
        Combo(s, "显示模式", "瀑布流按原图比例紧密拼接。", new[] { "规整网格", "瀑布流" }, () => Draft.DisplayMode, v => Draft.DisplayMode = v);
        Range(s, "照片大小", "最小宽度越大，每行照片越少。", 160, 360, 20, () => Draft.CardMinWidth, v => Draft.CardMinWidth = v, "像素");
        Combo(s, "卡片圆角", "仅影响规整网格。", new[] { "小圆角（8）", "圆角（14）", "大圆角（20）" }, () => Draft.CardCornerRadius <= 8 ? 0 : Draft.CardCornerRadius >= 20 ? 2 : 1, v => Draft.CardCornerRadius = new[] { 8d, 14d, 20d }[v]);
        Toggle(s, "切换动画", "控制图库切换和照片预览过渡。", () => Draft.AnimationsEnabled, v => Draft.AnimationsEnabled = v);
        Range(s, "动画时长", "调整图库入场的节奏。", 150, 600, 25, () => Draft.AnimationDurationMs, v => Draft.AnimationDurationMs = v, "毫秒");
    }

    private void BuildPreview()
    {
        var s = AddSection("照片预览");
        s.Reset = () => { Draft.PreviewPaletteEnabled = true; Draft.PreviewInfoDefaultVisible = true; Draft.FilmstripMode = 0; Draft.SlideshowIntervalSeconds = 4; Draft.ActiveAppearanceProfileId = ""; };
        Toggle(s, "照片取色背景", "背景色随当前照片变化。", () => Draft.PreviewPaletteEnabled, v => Draft.PreviewPaletteEnabled = v);
        Toggle(s, "默认显示详情栏", "打开照片时显示文件信息、分类和评分。", () => Draft.PreviewInfoDefaultVisible, v => Draft.PreviewInfoDefaultVisible = v);
        Combo(s, "底部胶片条", "自动模式下将指针移到预览底部即可显示。", new[] { "始终显示", "靠近底部显示", "隐藏" }, () => Draft.FilmstripMode, v => Draft.FilmstripMode = v);
        Range(s, "幻灯片换图间隔", "在预览工具栏点击播放，或按空格键。", 2, 15, 1, () => Draft.SlideshowIntervalSeconds, v => Draft.SlideshowIntervalSeconds = (int)v, "秒");
        AddSetting(s, "对比选片", "多选 2–4 张照片后选择对比；也可在预览中对比相邻照片。数字 0–5 评分，F 收藏，Tab 切换当前照片。", Label("支持同步缩放与拖动，可随时关闭同步。"));
    }

    private void BuildFiles()
    {
        var s = AddSection("文件管理");
        s.Reset = () => { Draft.AutoScan = true; Draft.AutoScanIntervalMinutes = 5; };
        Toggle(s, "自动扫描", "定期检测监控文件夹中的新增图片。", () => Draft.AutoScan, v => Draft.AutoScan = v);
        Range(s, "扫描间隔", "开启自动扫描后生效。", 1, 60, 1, () => Draft.AutoScanIntervalMinutes, v => Draft.AutoScanIntervalMinutes = (int)v, "分钟");
        var body = new StackPanel { Spacing = 8 };
        var list = new ListView { Height = 120, SelectionMode = ListViewSelectionMode.Single };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "添加文件夹…", FontSize = 12 };
        var remove = new Button { Content = "移除选中", FontSize = 12, IsEnabled = false };
        var count = Label("");
        _refreshers.Add(() => { list.Items.Clear(); foreach (var path in Draft.WatchedFolders) list.Items.Add(path); count.Text = $"已选择 {Draft.WatchedFolders.Count} 个文件夹"; });
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem != null;
        add.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary }; picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null || Draft.WatchedFolders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) return;
            Draft.WatchedFolders.Add(folder.Path); RefreshControls(); ShowNotice("已暂存监控文件夹，点击保存后开始监控。");
        };
        remove.Click += (_, _) => { if (list.SelectedItem is string path) { Draft.WatchedFolders.Remove(path); RefreshControls(); ShowNotice("已从草稿移除监控文件夹，点击保存生效。"); } };
        buttons.Children.Add(add); buttons.Children.Add(remove); body.Children.Add(count); body.Children.Add(list); body.Children.Add(buttons);
        AddSetting(s, "监控文件夹", "文件夹的添加与移除也支持取消。", body);
    }

    private void BuildUpdates()
    {
        var s = AddSection("关于与更新");
        s.Reset = () => { Draft.AutoCheckUpdates = true; Draft.UpdateChannel = "Stable"; Draft.UsePublicUpdateProxy = true; Draft.LastUpdateCheckUtc = DateTime.MinValue; };
        Combo(s, "更新通道", "Stable：稳定发行版。Nightly：每日测试版，可能有未验证的问题；可切回 Stable。", new[] { "Stable · 稳定版", "Nightly · 每日测试版" },
            () => Draft.UpdateChannel == "Nightly" ? 1 : 0, v => { Draft.UpdateChannel = v == 1 ? "Nightly" : "Stable"; Draft.LastUpdateCheckUtc = DateTime.MinValue; });
        Toggle(s, "公益下载代理", "直连失败后尝试 gh-proxy.org。仅传输公开更新包，不上传照片或令牌；仍验证 GitHub 直接提供的 SHA256。公共服务不保证可用。", () => Draft.UsePublicUpdateProxy, v => Draft.UsePublicUpdateProxy = v);
        Toggle(s, "自动检查更新", "启动及运行期间每 12 小时检查一次；只提示，不会自动安装。", () => Draft.AutoCheckUpdates, v => Draft.AutoCheckUpdates = v);
        var body = new StackPanel { Spacing = 8 };
        var status = Label($"当前版本：{UpdateService.CurrentDisplayVersion}");
        var check = new Button { Content = "检查更新", FontSize = 12 };
        var install = new Button { Content = "下载并更新", FontSize = 12, Visibility = Visibility.Collapsed };
        ReleaseInfo? release = null;
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false; status.Text = "正在检查更新…"; install.Visibility = Visibility.Collapsed;
            try
            {
                release = await UpdateService.CheckLatestAsync(Draft.UpdateChannel, Draft.UsePublicUpdateProxy);
                status.Text = release == null ? $"{Draft.UpdateChannel} 通道暂无可用更新（当前 {UpdateService.CurrentDisplayVersion}）" : $"发现 {release.Channel}：{release.DisplayVersion}";
                if (release != null && release.Channel != UpdateService.CurrentBuild.Channel) status.Text += "。切换发行通道可能回退版本；照片和图库数据保留。";
                install.Content = release?.Channel == "Nightly" ? "安装 Nightly 测试版" : "下载并更新";
                install.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { status.Text = "检查失败：" + ex.Message; }
            finally { check.IsEnabled = true; }
        };
        install.Click += (_, _) => { if (release != null && release.Channel == Draft.UpdateChannel) UpdateRequested?.Invoke(release with { UsePublicProxy = Draft.UsePublicUpdateProxy }); else ShowNotice("通道已修改，请重新检查更新。"); };
        body.Children.Add(status); body.Children.Add(check); body.Children.Add(install);
        AddSetting(s, "应用版本", "", body);
    }

    public void ResetAll()
    {
        foreach (var section in _sections) section.Reset();
        RefreshControls(); ShowNotice("已暂存全部默认值；已保存的方案和监控文件夹保留。点击保存生效。");
    }

    public void ShowNotice(string text) => _notice.Text = text;

    private void Changed(Section section)
    {
        if (_sections.IndexOf(section) <= 2) Draft.ActiveAppearanceProfileId = "";
        ShowNotice("有未保存的修改，点击保存生效。");
        UpdatePreview();
    }

    private void RefreshControls()
    {
        _refreshing = true;
        try { foreach (var update in _refreshers) update(); RefreshProfiles(Draft.ActiveAppearanceProfileId); }
        finally { _refreshing = false; }
        UpdatePreview();
    }

    private void RefreshProfiles(string id)
    {
        _profiles.Items.Clear();
        _profiles.Items.Add(new ComboBoxItem { Content = "选择一个方案…" });
        foreach (var profile in AppearanceProfile.BuiltIns().Concat(Draft.AppearanceProfiles))
            _profiles.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile });
        _profiles.SelectedIndex = 0;
        for (var i = 1; i < _profiles.Items.Count; i++)
            if (((ComboBoxItem)_profiles.Items[i]).Tag is AppearanceProfile p && p.Id == id) { _profiles.SelectedIndex = i; break; }
    }

    private void Filter()
    {
        var query = _search.Text.Trim();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = 0;
        for (var i = 0; i < _sections.Count; i++)
        {
            var section = _sections[i];
            var count = 0;
            foreach (var (card, keywords) in section.Cards)
            {
                var visible = words.Length == 0 || words.All(word => (section.Name + " " + keywords).Contains(word, StringComparison.OrdinalIgnoreCase));
                card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) count++;
            }
            var show = words.Length > 0 ? count > 0 : i == Math.Max(0, _navigation.SelectedIndex);
            section.Panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) matches += count;
        }
        _empty.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.ChangeView(null, 0, null, true);
    }

    private void UpdatePreview()
    {
        var dark = Draft.ThemeMode == 1 || (Draft.ThemeMode == -1 && ActualTheme == ElementTheme.Dark);
        _preview.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _preview.Background = new SolidColorBrush(Draft.BackgroundMode == 1 ? ParseColor(Draft.BackgroundColor) : dark ? Colors.Black : Colors.White);
        if (Draft.BackgroundMode == 2 && File.Exists(Draft.BackgroundImagePath))
            _preview.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(Draft.BackgroundImagePath)) { DecodePixelWidth = 600 }, Stretch = Stretch.UniformToFill };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Draft.GetAccentColor()) });
        body.Children.Add(new TextBlock { Text = $"haruphoto · {(Draft.DisplayMode == 1 ? "瀑布流" : "规整网格")} · {Draft.CardMinWidth:0}px", FontSize = 13, Foreground = new SolidColorBrush(dark ? Colors.White : Colors.Black) });
        var gallery = new Grid { ColumnSpacing = Draft.DisplayMode == 1 ? 0 : 6 };
        var columns = Draft.CardMinWidth >= 280 ? 2 : 3;
        for (var col = 0; col < columns; col++)
        {
            gallery.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { Spacing = Draft.DisplayMode == 1 ? 0 : 6 };
            for (var row = 0; row < 2; row++)
            {
                var photo = _samplePhotos.ElementAtOrDefault(col + row * columns);
                var cell = new Border
                {
                    Height = Draft.DisplayMode == 1 ? 40 + ((col + row) % 3) * 12 : 52,
                    CornerRadius = new CornerRadius(Draft.DisplayMode == 1 ? 0 : Draft.CardCornerRadius / 2),
                    Background = new SolidColorBrush(Draft.GetAccentColor()),
                };
                if (photo != null) cell.Child = new Image { Source = photo.ThumbnailSource, Stretch = Stretch.UniformToFill };
                stack.Children.Add(cell);
            }
            Grid.SetColumn(stack, col); gallery.Children.Add(stack);
        }
        body.Children.Add(gallery); _preview.Child = body;
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        try { var h = hex.TrimStart('#'); return Windows.UI.Color.FromArgb(255, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16)); }
        catch { return Windows.UI.Color.FromArgb(255, 91, 110, 174); }
    }
}
