using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI.Core;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private sealed class ComparePane
    {
        public required PhotoItem Photo { get; init; }
        public required Border Frame { get; init; }
        public required ScrollViewer View { get; init; }
        public required Grid Canvas { get; init; }
        public required Image Image { get; init; }
        public required RatingControl Rating { get; init; }
        public required Button Favorite { get; init; }
    }

    private readonly List<ComparePane> _comparePanes = new();
    private int _compareActive;
    private bool _compareUpdating;
    private PhotoItem? _compareReturnPhoto;

    private void CompareSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPaths.Count == 0)
        {
            if (!_batchMode) EnterBatchMode();
            SetStatusBarExpanded(true);
            StatusText.Text = "选择 2–4 张照片后点击「对比选片」";
            return;
        }
        var photos = _allPhotos.Where(p => _selectedPaths.Contains(p.FilePath)).ToList();
        if (photos.Count < 2 || photos.Count > 4)
        {
            StatusText.Text = $"已选 {photos.Count} 张，请选择 2–4 张进行对比";
            return;
        }
        OpenComparison(photos, null);
    }

    private void CompareAdjacent_Click(object sender, RoutedEventArgs e)
    {
        if (_currentView.Count < 2 || _previewIndex < 0 || _previewIndex >= _currentView.Count) return;
        var current = _currentView[_previewIndex];
        var other = _currentView[_previewIndex + 1 < _currentView.Count ? _previewIndex + 1 : _previewIndex - 1];
        OpenComparison(new[] { current, other }, current);
    }

    private void OpenComparison(IEnumerable<PhotoItem> photos, PhotoItem? returnPhoto)
    {
        ++_previewVisibilityRequest;
        ++_exifRequestSeq;
        _previewVisibilityAnimation?.Stop();
        _previewImageAnimation?.Stop();
        PreviewContentRoot.Opacity = 1;
        StopSlideshow();
        StopFilmstripLoading();
        ++_previewPaletteRequest;
        ++_previewTransitionRequest;
        _compareReturnPhoto = returnPhoto;
        if (returnPhoto != null)
        {
            PreviewOverlay.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            PreviewImageOutgoing.Source = null;
            PreviewFilmstrip.ItemsSource = null;
        }
        _comparePanes.Clear();
        ComparePhotosGrid.Children.Clear();
        ComparePhotosGrid.RowDefinitions.Clear();
        ComparePhotosGrid.ColumnDefinitions.Clear();
        var targets = photos.Distinct().Take(4).ToList();
        if (targets.Count < 2) return;
        ComparePhotosGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ComparePhotosGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (targets.Count > 2 ? 2 : 1); row++)
            ComparePhotosGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < targets.Count; i++)
        {
            var pane = CreateComparePane(targets[i], i);
            Grid.SetColumn(pane.Frame, i % 2); Grid.SetRow(pane.Frame, i / 2);
            ComparePhotosGrid.Children.Add(pane.Frame);
            _comparePanes.Add(pane);
        }
        CompareOverlay.Visibility = Visibility.Visible;
        SelectComparePane(0);
        _comparePanes[0].View.Focus(FocusState.Programmatic);
    }

    private ComparePane CreateComparePane(PhotoItem photo, int index)
    {
        var canvas = new Grid();
        var image = new Image { Stretch = Stretch.Uniform, Source = photo.ThumbnailSource };
        canvas.Children.Add(image);
        var viewport = new ScrollViewer
        {
            Content = canvas, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 8,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsTabStop = true,
        };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var stage = new Grid(); stage.Children.Add(viewport);
        var loading = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stage.Children.Add(loading); content.Children.Add(stage);
        var info = new StackPanel { Spacing = 6, Padding = new Thickness(12, 8, 12, 8) };
        info.Children.Add(new TextBlock { Text = photo.Filename, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = $"{photo.SizeText} · 修改于 {photo.DateText}", FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var rating = new RatingControl { MaxRating = 5, Value = photo.Rating };
        var favorite = new Button { Content = photo.IsFavorite ? "★ 已收藏" : "☆ 收藏", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(viewport, $"对比照片 {index + 1}：{photo.Filename}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rating, $"照片 {index + 1} 评分");
        actions.Children.Add(rating); actions.Children.Add(favorite); info.Children.Add(actions);
        Grid.SetRow(info, 1); content.Children.Add(info);
        var frame = new Border
        {
            Child = content, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 27, 29, 36)),
        };
        var pane = new ComparePane { Photo = photo, Frame = frame, View = viewport, Canvas = canvas, Image = image, Rating = rating, Favorite = favorite };
        void SizeCanvas()
        {
            canvas.Width = Math.Max(1, viewport.ActualWidth);
            canvas.Height = Math.Max(1, viewport.ActualHeight);
        }
        viewport.SizeChanged += (_, _) => SizeCanvas();
        frame.PointerPressed += (_, _) => SelectComparePane(index);
        // 只传播当前操作的窗格，防止从同步触发的 ViewChanged 反向传播。
        viewport.PointerEntered += (_, _) => SelectComparePane(index);
        viewport.GotFocus += (_, _) => SelectComparePane(index);
        viewport.ViewChanged += (_, _) => { if (_compareActive == index && !_compareUpdating) SyncCompareViews(pane); };
        rating.ValueChanged += (_, _) =>
        {
            if (_compareUpdating) return;
            SelectComparePane(index);
            photo.Rating = Math.Clamp((int)Math.Round(rating.Value), 0, 5);
            ScheduleSave(); UpdateStats();
        };
        favorite.Click += (_, _) => { SelectComparePane(index); photo.IsFavorite = !photo.IsFavorite; favorite.Content = photo.IsFavorite ? "★ 已收藏" : "☆ 收藏"; ScheduleSave(); UpdateStats(); };
        var bitmap = new BitmapImage { DecodePixelWidth = 2400 };
        bitmap.ImageOpened += (_, _) => { loading.IsActive = false; loading.Visibility = Visibility.Collapsed; SizeCanvas(); };
        bitmap.ImageFailed += (_, _) =>
        {
            loading.IsActive = false; loading.Visibility = Visibility.Collapsed;
            stage.Children.Add(new TextBlock { Text = "无法读取照片文件", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Colors.White) });
        };
        image.Source = bitmap;
        try { bitmap.UriSource = new Uri(photo.FilePath); } catch { loading.IsActive = false; }
        return pane;
    }

    private void SelectComparePane(int index)
    {
        if (index < 0 || index >= _comparePanes.Count) return;
        _compareActive = index;
        for (var i = 0; i < _comparePanes.Count; i++)
            _comparePanes[i].Frame.BorderBrush = new SolidColorBrush(i == index ? _settings.GetAccentColor() : Windows.UI.Color.FromArgb(255, 50, 53, 63));
        CompareStatus.Text = $"{_comparePanes.Count} 张照片 · 当前：{_comparePanes[index].Photo.Filename}";
    }

    private void SyncCompareViews(ComparePane source)
    {
        if (CompareSyncCheck.IsChecked != true || CompareOverlay.Visibility != Visibility.Visible) return;
        var x = source.View.ScrollableWidth > 0 ? source.View.HorizontalOffset / source.View.ScrollableWidth : 0;
        var y = source.View.ScrollableHeight > 0 ? source.View.VerticalOffset / source.View.ScrollableHeight : 0;
        _compareUpdating = true;
        try
        {
            foreach (var target in _comparePanes.Where(p => p != source))
            {
                var horizontal = Math.Max(0, target.Canvas.Width * source.View.ZoomFactor - target.View.ViewportWidth) * x;
                var vertical = Math.Max(0, target.Canvas.Height * source.View.ZoomFactor - target.View.ViewportHeight) * y;
                if (Math.Abs(target.View.ZoomFactor - source.View.ZoomFactor) > .001 || Math.Abs(target.View.HorizontalOffset - horizontal) > .5 || Math.Abs(target.View.VerticalOffset - vertical) > .5)
                    target.View.ChangeView(horizontal, vertical, source.View.ZoomFactor, true);
            }
        }
        finally { _compareUpdating = false; }
    }

    private void CompareSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_comparePanes.Count > _compareActive) SyncCompareViews(_comparePanes[_compareActive]);
    }

    private void ChangeCompareZoom(float multiplier)
    {
        if (_comparePanes.Count == 0) return;
        var pane = _comparePanes[_compareActive];
        pane.View.ChangeView(null, null, Math.Clamp(pane.View.ZoomFactor * multiplier, 1, 8), true);
    }

    private void CompareZoomIn_Click(object sender, RoutedEventArgs e) => ChangeCompareZoom(1.25f);
    private void CompareZoomOut_Click(object sender, RoutedEventArgs e) => ChangeCompareZoom(.8f);
    private void CompareFit_Click(object sender, RoutedEventArgs e)
    {
        _compareUpdating = true;
        try { foreach (var pane in _comparePanes) pane.View.ChangeView(0, 0, 1, true); }
        finally { _compareUpdating = false; }
    }

    private void CompareClose_Click(object sender, RoutedEventArgs e) => CloseComparison();

    private void CloseComparison()
    {
        CompareOverlay.Visibility = Visibility.Collapsed;
        var returnPhoto = _compareReturnPhoto;
        _compareReturnPhoto = null;
        foreach (var pane in _comparePanes) pane.Image.Source = null;
        _comparePanes.Clear(); ComparePhotosGrid.Children.Clear();
        RefreshPhotos();
        if (returnPhoto != null)
        {
            var photo = _currentView.Contains(returnPhoto) ? returnPhoto : _currentView.FirstOrDefault();
            if (photo != null) OpenPreview(photo);
        }
    }

    private void Compare_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_comparePanes.Count == 0 || FocusManager.GetFocusedElement() is TextBox) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var key = (int)e.Key;
        if (key >= (int)VirtualKey.Number0 && key <= (int)VirtualKey.Number5 && !ctrl)
        {
            _comparePanes[_compareActive].Rating.Value = key - (int)VirtualKey.Number0;
            e.Handled = true; return;
        }
        switch (e.Key)
        {
            case VirtualKey.Escape: CloseComparison(); e.Handled = true; break;
            case VirtualKey.Number0 when ctrl: CompareFit_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case VirtualKey.F:
                var pane = _comparePanes[_compareActive];
                pane.Photo.IsFavorite = !pane.Photo.IsFavorite;
                pane.Favorite.Content = pane.Photo.IsFavorite ? "★ 已收藏" : "☆ 收藏";
                ScheduleSave(); UpdateStats(); e.Handled = true; break;
            case VirtualKey.Tab:
                var backwards = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                SelectComparePane((_compareActive + (backwards ? _comparePanes.Count - 1 : 1)) % _comparePanes.Count);
                _comparePanes[_compareActive].View.Focus(FocusState.Programmatic);
                e.Handled = true; break;
        }
    }
}
