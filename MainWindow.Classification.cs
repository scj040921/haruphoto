using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private bool _classifying;
    private async void AutoClassify_Click(object sender, RoutedEventArgs e)
    {
        if (_classifying || _allPhotos.Count == 0) return;
        _classifying = true;
        try
        {
            var scope = new ComboBox { Header = "范围", HorizontalAlignment = HorizontalAlignment.Stretch };
            scope.Items.Add(new ComboBoxItem { Content = $"已选照片（{_selectedPaths.Count} 张）", IsEnabled = _selectedPaths.Count > 0 });
            scope.Items.Add(new ComboBoxItem { Content = $"当前筛选结果（{_currentView.Count} 张）" });
            scope.SelectedIndex = _selectedPaths.Count > 0 ? 0 : 1;
            var rule = new ComboBox { Header = "规则", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var label in new[] { "修改日期月份", "所属文件夹", "图片格式", "横向 / 竖向 / 方形", "文件名包含关键词" }) rule.Items.Add(label);
            rule.SelectedIndex = 0;
            var keyword = new TextBox { Header = "文件名关键词", PlaceholderText = "如：旅行、截图、IMG" };
            var category = new TextBox { Header = "关键词对应分类", PlaceholderText = "如：旅行回忆" };
            keyword.Visibility = category.Visibility = Visibility.Collapsed;
            rule.SelectionChanged += (_, _) => keyword.Visibility = category.Visibility = rule.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            var preserve = new CheckBox { Content = "仅处理未分类照片（保留手动分类）", IsChecked = true };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(scope); panel.Children.Add(rule); panel.Children.Add(keyword); panel.Children.Add(category); panel.Children.Add(preserve);
            panel.Children.Add(new TextBlock { Text = "在本机按规则整理，不上传图片。不进行人物或场景 AI 识别。月份沿用文件修改日期。", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { Title = "规则自动分类", Content = panel, PrimaryButtonText = "生成预览", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (rule.SelectedIndex == 4 && (string.IsNullOrWhiteSpace(keyword.Text) || string.IsNullOrWhiteSpace(category.Text))) { StatusText.Text = "请填写关键词和分类名称"; return; }
            var targets = (scope.SelectedIndex == 0 ? _allPhotos.Where(p => _selectedPaths.Contains(p.FilePath)) : _currentView)
                .Where(p => preserve.IsChecked != true || string.IsNullOrWhiteSpace(p.Category)).ToArray();
            var proposals = new Dictionary<PhotoItem, string>();
            BusyRing.Visibility = Visibility.Visible;
            foreach (var photo in targets)
            {
                if (_windowLifetime.IsCancellationRequested) return;
                double? aspect = null;
                if (rule.SelectedIndex == 3)
                {
                    try
                    {
                        using var stream = File.OpenRead(photo.FilePath);
                        using var input = stream.AsRandomAccessStream();
                        var decoder = await BitmapDecoder.CreateAsync(input);
                        aspect = decoder.OrientedPixelHeight == 0 ? null : decoder.OrientedPixelWidth / (double)decoder.OrientedPixelHeight;
                    }
                    catch { /* 不可读文件保持原分类，不猜测比例。 */ }
                }
                var name = PhotoClassification.Suggest(photo, rule.SelectedIndex, keyword.Text, category.Text, aspect);
                if (name.Length > 0 && name != photo.Category) proposals[photo] = name;
            }
            BusyRing.Visibility = Visibility.Collapsed;
            var summary = string.Join("\n", proposals.GroupBy(p => p.Value).Take(20).Select(g => $"{g.Key}：{g.Count()} 张"));
            var confirm = new ContentDialog { Title = $"分类预览 · {proposals.Count} 张", Content = new ScrollViewer { MaxHeight = 350, Content = new TextBlock { Text = summary.Length > 0 ? summary + "\n\n确认后应用，可通过底栏撤销。" : "没有需要修改的照片。", TextWrapping = TextWrapping.Wrap } },
                PrimaryButtonText = "应用分类", IsPrimaryButtonEnabled = proposals.Count > 0, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            ApplyClassification(proposals);
        }
        catch (Exception ex) { StatusText.Text = "分类未完成：" + ex.Message; }
        finally { _classifying = false; if (!_windowClosing) BusyRing.Visibility = Visibility.Collapsed; }
    }

    private void ApplyClassification(Dictionary<PhotoItem, string> proposals)
    {
        var before = proposals.Keys.ToDictionary(p => p, p => p.Category);
        foreach (var pair in proposals) pair.Key.Category = pair.Value;
        RegisterUndo($"自动分类 {proposals.Count} 张照片", () => { foreach (var pair in before) pair.Key.Category = pair.Value; RebuildCategories(); });
        RebuildCategories(); RefreshPhotos(); ScheduleSave(); SetStatusBarExpanded(true);
        StatusText.Text = $"已按规则分类 {proposals.Count} 张，可撤销";
    }
}
