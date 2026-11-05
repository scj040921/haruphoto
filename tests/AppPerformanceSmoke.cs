// 仅 PerformanceSmoke=true 的 QA 构建编译此文件，正式程序不包含自动操作。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private async Task RunLibraryFailureSmokeAsync()
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Check(!_libraryLoaded && _libraryLoadFailed && LibraryLoadNotice.IsOpen && !ImportFilesButton.IsEnabled && !ImportFolderButton.IsEnabled,
                "failed library load displays an error and disables imports");
            var original = await File.ReadAllBytesAsync(LibraryStore.LibraryPath);
            Check(!await ImportPathsAsync(new[] { Path.Combine(AppPaths.DataDirectory, "images", "test-000.png") }, "test") &&
                original.SequenceEqual(await File.ReadAllBytesAsync(LibraryStore.LibraryPath)), "failed load preserves the original index and blocks import");
            await File.WriteAllTextAsync(LibraryStore.LibraryPath, "{\"version\":1,\"photos\":[]}");
            await InitAsync();
            Check(_libraryLoaded && !_libraryLoadFailed && !LibraryLoadNotice.IsOpen && ImportFilesButton.IsEnabled,
                "successful retry restores library controls");
            File.Delete(LibraryStore.LibraryPath); Directory.CreateDirectory(LibraryStore.LibraryPath);
            await SaveLibraryWithNoticeAsync();
            Check(LibrarySaveNotice.IsOpen, "save failure shows a recoverable warning");
            Directory.Delete(LibraryStore.LibraryPath);
            await SaveLibraryWithNoticeAsync();
            Check(!LibrarySaveNotice.IsOpen && File.Exists(LibraryStore.LibraryPath), "retry saves and clears the warning");
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "library-failure-smoke.json"), JsonSerializer.Serialize(new { success = true, checks }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "library-failure-smoke.json"), JsonSerializer.Serialize(new { success = false, checks, error = ex.ToString() })); }
        finally { Close(); }
    }

    private async Task RunPerformanceSmokeAsync()
    {
        var checks = new List<string>();
        var watch = Stopwatch.StartNew();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        async Task WaitFor(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("UI readiness timeout"); await Task.Delay(50); }
        }
        try
        {
            await WaitFor(() => Content.XamlRoot != null);
            Check(_currentView.Count >= 400, "large isolated photo fixture loaded");
            await Task.Delay(250);
            Windows.Foundation.Rect Bounds(FrameworkElement element, UIElement relative)
            {
                var point = element.TransformToVisual(relative).TransformPoint(new Windows.Foundation.Point(0, 0));
                return new Windows.Foundation.Rect(point.X, point.Y, element.ActualWidth, element.ActualHeight);
            }
            async Task CheckLibraryLayout(double width)
            {
                MainScroll.ChangeView(null, 0, null, true);
                await WaitFor(() => MainScroll.VerticalOffset < .5);
                GallerySurface.Width = width;
                GallerySurface.UpdateLayout(); await Task.Delay(150);
                var controls = new FrameworkElement[] { GalleryTitleText, LibraryNameText, SearchBox, SortCombo, FilterMenuButton, PaginationGroup };
                var bounds = controls.Select(c => Bounds(c, TopBarOverlay)).ToArray();
                var fits = bounds.All(r => r.X >= 0 && r.Right <= TopBarOverlay.ActualWidth + 1 && r.Width > 25);
                for (var a = 0; a < bounds.Length; a++)
                for (var b = a + 1; b < bounds.Length; b++)
                    fits &= bounds[a].Right <= bounds[b].Left + 1 || bounds[b].Right <= bounds[a].Left + 1 ||
                            bounds[a].Bottom <= bounds[b].Top + 1 || bounds[b].Bottom <= bounds[a].Top + 1;
                Check(fits, $"header search, filters and pager fit without overlap at {TopBarOverlay.ActualWidth:F0}px");
                Check(Bounds(GalleryContentGrid, GallerySurface).Top + MainScroll.VerticalOffset >= Bounds(TopBarOverlay, GallerySurface).Bottom + 10,
                      $"first photo starts after the measured floating header without a fixed offset (contentTop={Bounds(GalleryContentGrid, GallerySurface).Top:F1}, headerBottom={Bounds(TopBarOverlay, GallerySurface).Bottom:F1}, scrollOffset={MainScroll.VerticalOffset:F1}, inset={GalleryContentGrid.Margin.Top:F1})");
                Check(Math.Abs(Bounds(GalleryContentGrid, GallerySurface).Left - Bounds(TopBarOverlay, GallerySurface).Left) < 1,
                      "gallery photos align with the floating header edge");
            }
            await CheckLibraryLayout(double.NaN);
            await _thumbnailBatchTask;
            await Task.Delay(300);
            var initialRealizedCards = Enumerable.Range(0, _photos.Count).Count(i => PhotoGrid.TryGetElement(i) != null);
            Check(initialRealizedCards > 0 && initialRealizedCards < _photos.Count, "gallery prepares viewport cards instead of the entire page");
            var masonryLayout = (MasonryLayout)PhotoGrid.Layout;
            var geometryBuildsBeforeScroll = masonryLayout.GeometryBuilds;
            MainScroll.ChangeView(null, 1000, null, true); await Task.Delay(250);
            Check(Enumerable.Range(10, _photos.Count - 10).Any(i => PhotoGrid.TryGetElement(i) is FrameworkElement item &&
                    Bounds(item, MainScroll).Bottom > 0 && Bounds(item, MainScroll).Top < MainScroll.ActualHeight),
                "middle of masonry page realizes the matching visible photos");
            MainScroll.ChangeView(null, MainScroll.ScrollableHeight, null, true); await Task.Delay(250);
            Check(Enumerable.Range(0, _photos.Count).Select(i => PhotoGrid.TryGetElement(i)).OfType<FrameworkElement>()
                    .Any(item => { var r = Bounds(item, MainScroll); return r.Bottom > 0 && r.Top < MainScroll.ActualHeight; }),
                $"scrolling realizes photos intersecting the viewport, including extreme aspect ratios; offset={MainScroll.VerticalOffset:F0}/{MainScroll.ScrollableHeight:F0}; {((MasonryLayout)PhotoGrid.Layout).Diagnostics}");
            Check(masonryLayout.GeometryBuilds == geometryBuildsBeforeScroll, "scrolling reuses masonry geometry");
            await CaptureFeatureSnapshotAsync("masonry-bottom.png");
            MainScroll.ChangeView(null, 0, null, true); await Task.Delay(250);
            Check(PhotoGrid.TryGetElement(0) != null, "returning to top restores the first masonry card");
            MoveFilterIndicator(SortCombo, false); var firstIndicatorX = Canvas.GetLeft(FilterIndicator);
            MoveFilterIndicator(FilterMenuButton); await Task.Delay(250);
            Check(FilterIndicator.Visibility == Visibility.Visible && Canvas.GetLeft(FilterIndicator) > firstIndicatorX && _indicatorTarget == FilterMenuButton,
                "filter underline follows the selected control without moving its layout");
            var cardHost = (Grid)PhotoGrid.TryGetElement(0);
            cardHost.IsHitTestVisible = false;
            var cardRoot = (Border)cardHost.Children[0];
            Check(cardHost.Opacity == 1 && cardRoot.Shadow == null, "realized cards appear without per-card animation or shadow");
            var cardOutline = (Border)((Grid)cardRoot.Child).Children.Last();
            var cardState = _cardAnimations.GetValue(cardHost, _ => new CardAnimationState());
            SetCardHighlight(cardRoot, cardState, true); await Task.Delay(220);
            Check(cardOutline.Opacity > .95 && cardHost.Margin == new Thickness(0), "hover highlights the photo without shifting layout");
            SetCardHighlight(cardRoot, cardState, false); await WaitFor(() => cardOutline.Opacity < .05);
            Check(cardOutline.Opacity < .05, "hover exit restores the photo outline");
            _settings.AnimationsEnabled = false;
            SetCardHighlight(cardRoot, cardState, true);
            Check(cardOutline.Opacity == 1, "disabled animations keep immediate photo feedback");
            SetCardHighlight(cardRoot, cardState, false); _settings.AnimationsEnabled = true;
            cardHost.IsHitTestVisible = true;
            var unfavoritedHost = Enumerable.Range(0, _photos.Count).Select(i => PhotoGrid.TryGetElement(i)).OfType<Grid>()
                .First(host => host.DataContext is PhotoItem photo && !photo.IsFavorite);
            var unfavoritedCard = (Border)unfavoritedHost.Children[0];
            // Prevent a real pointer entering the test card from racing the synthetic hover check.
            unfavoritedHost.IsHitTestVisible = false;
            var favoriteAction = FindVisual<Button>(unfavoritedCard).Single(b => b.Name == "CardFavoriteAction");
            var unfavoritedState = _cardAnimations.GetValue(unfavoritedHost, _ => new CardAnimationState());
            SetCardFavoriteReveal(unfavoritedCard, unfavoritedState, false);
            await WaitFor(() => favoriteAction.Opacity < .05);
            Check(favoriteAction.Opacity < .05 && favoriteAction.IsTabStop && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(favoriteAction) == "收藏照片",
                "unfavorited photos keep a quiet but keyboard-accessible favorite action");
            SetCardFavoriteReveal(unfavoritedCard, unfavoritedState, true); await WaitFor(() => favoriteAction.Opacity > .95);
            Check(favoriteAction.Opacity > .95, "favorite action becomes visible when the card is engaged");
            SetCardFavoriteReveal(unfavoritedCard, unfavoritedState, false); await Task.Delay(50);
            await WaitFor(() => favoriteAction.Opacity < .05);
            Check(favoriteAction.Opacity < .05, "favorite action returns to its bound resting state");
            unfavoritedHost.IsHitTestVisible = true;
            ShowLibrarySection(LibrarySection.Statistics); ShowLibrarySection(LibrarySection.Organize); ShowLibrarySection(LibrarySection.Gallery);
            await Task.Delay(300);
            Check(GallerySurface.Visibility == Visibility.Visible && GallerySurface.Opacity > .99 && StatsScroll.Opacity == 1 && OrganizeScroll.Opacity == 1,
                "rapid navigation leaves the current page visible without stale animation");
            await CaptureFeatureSnapshotAsync("library-dark.png");
            await CheckLibraryLayout(900);
            await CheckLibraryLayout(860);
            await CheckLibraryLayout(640);
            await CheckLibraryLayout(480);
            await CaptureFeatureSnapshotAsync("library-compact.png");
            GallerySurface.Width = double.NaN;
            GallerySurface.UpdateLayout();
            var unfilteredHeaderHeight = TopBarOverlay.ActualHeight;
            FilterMenuButton.Flyout.ShowAt(FilterMenuButton); await Task.Delay(100);
            Check(FilterRatingCombo.ActualWidth > 200 && FilterDateCombo.ActualWidth > 200 && FavOnlyCheckBox.ActualWidth > 200,
                  "secondary filters open as readable full width controls in one menu");
            FilterRatingCombo.SelectedIndex = 4; FilterDateCombo.SelectedIndex = 1; FavOnlyCheckBox.IsChecked = true;
            await _thumbnailBatchTask;
            Check(FilterCountText.Text == "3" && FilterCountBadge.Visibility == Visibility.Visible &&
                  _currentView.Count > 0 && _currentView.All(p => p.Rating >= 4 && p.IsFavorite) &&
                  Math.Abs(TopBarOverlay.ActualHeight - unfilteredHeaderHeight) < 1,
                  "filter menu applies criteria and badge without adding another header row");
            await CaptureFeatureSnapshotAsync("library-filter-menu.png");
            FilterDone_Click(this, new RoutedEventArgs());
            ClearFilters_Click(this, new RoutedEventArgs()); await _thumbnailBatchTask;
            Check(FilterCountBadge.Visibility == Visibility.Collapsed && _currentView.Count == _allPhotos.Count,
                  "resetting the filter menu restores all photos and clears its badge");
            Check(Bounds(GalleryContentGrid, GallerySurface).Top >= Bounds(TopBarOverlay, GallerySurface).Bottom + 10,
                  "filter menu leaves the first photo below the floating header");
            var headerTop = Bounds(TopBarOverlay, GallerySurface).Top;
            var firstPhotoTop = Bounds(GalleryContentGrid, GallerySurface).Top;
            MainScroll.ChangeView(null, 180, null, true); await Task.Delay(100);
            Check(MainScroll.VerticalOffset > 100 && Math.Abs(Bounds(TopBarOverlay, GallerySurface).Top - headerTop) < 1 &&
                  Bounds(GalleryContentGrid, GallerySurface).Top < firstPhotoTop - 100,
                  "floating header stays fixed while photos scroll underneath");
            MainScroll.ChangeView(null, 0, null, true); await Task.Delay(100);
            Check(ScrollGalleryWheel(-120) && ScrollGalleryWheel(-15) && _galleryWheelTarget == 135, "wheel input accumulates fractional steps without restarting motion");
            await WaitFor(() => Math.Abs(MainScroll.VerticalOffset - 135) < 1 && !_galleryWheelRunning);
            ScrollGalleryWheel(-120); StopGalleryWheel();
            Check(!_galleryWheelRunning && _galleryWheelTarget == null, "manual interaction cancels wheel motion without a stale target");
            ScrollGalleryWheel(-120);
            var reversalStart = MainScroll.VerticalOffset;
            ScrollGalleryWheel(15);
            Check(_galleryWheelTarget == Math.Max(0, reversalStart - 15), "reversing wheel input discards the old direction target");
            StopGalleryWheel();
            MainScroll.ChangeView(null, 0, null, true); await Task.Delay(200);
            Check(MainScroll.ScrollableHeight > 0, "native gallery scrolling remains available");
            CompleteGalleryScroll();
            await Task.Delay(200);
            var measureBefore = _testGalleryMeasureCount;
            for (var i = 0; i < 20; i++) RequestGalleryMeasure();
            await Task.Delay(150);
            Check(_testGalleryMeasureCount == measureBefore + 1, "thumbnail completions coalesce into one gallery layout update");
            var geometryBeforePending = ((MasonryLayout)PhotoGrid.Layout).GeometryBuilds;
            _galleryScrolling = true; RequestGalleryMeasure();
            PhotoGrid.InvalidateMeasure(); PhotoGrid.UpdateLayout();
            Check(_galleryMeasurePending && ((MasonryLayout)PhotoGrid.Layout).GeometryBuilds == geometryBeforePending,
                "thumbnail geometry stays stable when layout is measured during scrolling");
            CompleteGalleryScroll(); Check(!_galleryMeasurePending && !_galleryScrolling, "deferred layout resumes when scrolling ends");
            await WaitFor(() => ((MasonryLayout)PhotoGrid.Layout).GeometryBuilds > geometryBeforePending);
            MainScroll.ChangeView(null, 0, null, true); await Task.Delay(100);
            double Luminance(Windows.UI.Color color)
            {
                double Linear(byte channel) { var x = channel / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
                return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
            }
            foreach (var theme in new[] { 0, 1 })
            {
                _settings.ThemeMode = theme; ApplyTheme(); await _thumbnailBatchTask; await Task.Delay(250);
                if (theme == 0) await CaptureFeatureSnapshotAsync("library-light.png");
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().Single(i => i.Tag as string == "Stats");
                await Task.Delay(250);
                Check(GallerySurface.Visibility == Visibility.Collapsed && StatsScroll.Visibility == Visibility.Visible &&
                      OrganizeScroll.Visibility == Visibility.Collapsed && StatsGrid.Children.Count == 6,
                      $"statistics owns its page without gallery header or status overlays in theme {theme}");
                Check(StatsGrid.Children.OfType<Border>().All(card =>
                {
                    var surface = ((SolidColorBrush)card.Background).Color;
                    return (theme == 0 ? surface.R > 200 : surface.R < 80) && FindVisual<TextBlock>(card).All(text =>
                    {
                        var foreground = ((SolidColorBrush)text.Foreground).Color;
                        var a = Luminance(surface); var b = Luminance(foreground);
                        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5;
                    });
                }), $"statistics cards follow window theme and keep readable text contrast in theme {theme}");
                await CaptureFeatureSnapshotAsync(theme == 0 ? "statistics-light.png" : "statistics-dark.png");
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().Single(i => i.Tag as string == "Organize");
                Check(GallerySurface.Visibility == Visibility.Collapsed && StatsScroll.Visibility == Visibility.Collapsed &&
                      OrganizeScroll.Visibility == Visibility.Visible, "organize page excludes gallery controls");
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().Single(i => i.Tag as string == "All");
                Check(GallerySurface.Visibility == Visibility.Visible && StatsScroll.Visibility == Visibility.Collapsed &&
                      OrganizeScroll.Visibility == Visibility.Collapsed, "returning to photos restores the full gallery page");
            }
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().Single(i => i.Tag as string == "Organize");
            var organizeResult = OrganizeItems.Items.Cast<DependencyObject>().SelectMany(FindButtons).Single(b => b.Tag as string == "CatUncategorized");
            OrganizeFilter_Click(organizeResult, new RoutedEventArgs()); await _thumbnailBatchTask;
            Check(GallerySurface.Visibility == Visibility.Visible && (NavView.SelectedItem as NavigationViewItem)?.Tag as string == "All" &&
                  _categoryFilter == "__UNCATEGORIZED__" && _currentView.Count < _allPhotos.Count,
                  "opening an organize result synchronizes navigation without discarding its photo filter");
            ClearFilters_Click(this, new RoutedEventArgs()); await _thumbnailBatchTask;
            PageInfoButton.Flyout.ShowAt(PageInfoButton); await Task.Delay(100);
            PageJumpBox.Value = 2; JumpToPage(); await _thumbnailBatchTask;
            Check(_currentPage == 1 && PageInfo.Text.StartsWith("2 /"), "page menu jump still changes the actual gallery page");
            _currentPage = 0; RenderCurrentPage(); await _thumbnailBatchTask;
            var view = _currentView;
            for(var page=0;page<=(_currentView.Count-1)/PageSize;page++)
            {
                _currentPage=page; RenderCurrentPage(); await _thumbnailBatchTask;
                Check(ReferenceEquals(view,_currentView), "page "+page+" reuses sorted view");
                Check(_allPhotos.Count(p=>p.ThumbnailSource!=null)<=160,"page "+page+" obeys thumbnail memory bound");
            }
            Check(_allPhotos.Any(p=>p.ThumbnailStatus=="无法读取"),"corrupt thumbnail reports readable failure state");
            Check(view[0].ThumbnailSource==null,"old page thumbnail released after extended browsing");
            _currentPage=0; RenderCurrentPage(); await _thumbnailBatchTask;
            Check(view[0].ThumbnailSource!=null,"returning page reloads evicted thumbnail");
            Check(view.Take(3).All(p=>p.ThumbnailSource!.PixelWidth>0&&p.ThumbnailAspectRatio>0),"landscape portrait and square dimensions remain available");
            Check(view.Take(5).All(p=>p.ThumbnailSource!.PixelWidth<=400&&p.ThumbnailSource.PixelHeight<=1200),"extreme aspect ratios obey decode size bounds");
            var filename=view[0].Filename; view[0].Filename="renamed-sample.png";
            RefreshPhotos(); await Task.Delay(100);
            Check(FindVisual<TextBlock>(PhotoGrid).Any(t=>ReferenceEquals(t.DataContext,view[0])&&t.Text==view[0].DisplayName),"rename updates label on reused page");
            view[0].Filename=filename;
            _settings.DisplayMode=0; _settings.CardCornerRadius=22;
            ApplyGalleryLayout(); RefreshPhotos(forceRender:true); await _thumbnailBatchTask; await Task.Delay(100);
            Check(FindVisual<Border>(PhotoGrid).Any(b=>b.Name=="CardRoot"&&b.CornerRadius.TopLeft==22),"appearance settings repaint existing cards");
            _settings.DisplayMode=1; ApplyGalleryLayout(); RefreshPhotos(forceRender:true); await _thumbnailBatchTask; await Task.Delay(100);
            Check(Enumerable.Range(0, _photos.Count).Select(i => PhotoGrid.TryGetElement(i)).OfType<DependencyObject>()
                .SelectMany(FindVisual<Border>).Where(b=>b.Name=="CardRoot").All(b=>b.CornerRadius.TopLeft==0),"switching back to masonry restores square joined cards");

            _previewRevealOrigin = new Windows.Foundation.Point(100, 100); OpenPreview(view[120]);
            Check(PreviewRippleLayer.Visibility == Visibility.Visible,
                "photo preview begins with a circle at the chosen origin");
            await WaitFor(() => PreviewRippleLayer.Visibility == Visibility.Collapsed);
            Check(PreviewRippleLayer.Visibility == Visibility.Collapsed,
                "circle transition releases its overlay after completion");
            _settings.AnimationsEnabled = false; _previewRevealOrigin = new Windows.Foundation.Point(50, 50);
            StartPreviewReveal(_previewVisibilityRequest);
            Check(PreviewRippleLayer.Visibility == Visibility.Collapsed, "disabled animations show preview without a circular overlay");
            _settings.AnimationsEnabled = true;
            await WaitFor(()=>!_previewIsLoading);
            Check(PreviewTitleBar.Text == view[120].Filename, "preview identifies the displayed photo");
            var moreMenu = (MenuFlyout)PreviewMoreButton.Flyout;
            moreMenu.ShowAt(PreviewMoreButton); await Task.Delay(150);
            Check(moreMenu.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).SequenceEqual(new[] { "旋转预览", "在外部应用中编辑", "复制图片" }),
                "preview more menu exposes the secondary photo actions");
            moreMenu.Hide();
            await CaptureFeatureSnapshotAsync("preview-detail.png");
            await EnsureFilmstripThumbnailAsync(view[120],_filmstripCts!.Token);
            Check(view[120].ThumbnailSource!=null,"filmstrip loads a photo outside current gallery page");
            var sharedPhoto = view[300]; sharedPhoto.ThumbnailSource = null;
            _testThumbnailPath = sharedPhoto.FilePath; _testThumbnailLoads = 0;
            var sharedLoad1 = EnsureFilmstripThumbnailAsync(sharedPhoto, _filmstripCts.Token);
            var sharedLoad2 = EnsureFilmstripThumbnailAsync(sharedPhoto, _filmstripCts.Token);
            await Task.WhenAll(sharedLoad1, sharedLoad2);
            Check(_testThumbnailLoads == 1 && sharedPhoto.ThumbnailSource != null,
                "preview and filmstrip share a pending thumbnail decode");
            _testThumbnailPath = null;
            await EnsureFilmstripThumbnailAsync(view[121],_filmstripCts.Token);
            await EnsureFilmstripThumbnailAsync(view[122],_filmstripCts.Token);
            await WaitFor(() => view[121].ThumbnailSource != null && view[122].ThumbnailSource != null);
            var decodeGate = _previewTestDecodeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldPreview = PreviewImage.Opacity >= .5 && _previewLastReady != null ? _previewLastReady : PreviewSoftLayer.Opacity > 0 && PreviewSoftImage.Source != null ? PreviewSoftImage.Source : _previewLastReady;
            ShowPreviewAt(121);
            await WaitFor(() => ReferenceEquals(PreviewSoftImage.Source, view[121].ThumbnailSource));
            await Task.Delay(350);
            Check(_previewIsLoading && ReferenceEquals(PreviewImageOutgoing.Source, oldPreview),
                "pending photo retains the previous frame and shows the target thumbnail without a loading prompt or spinner");
            Check(_previewFocusVisual?.IsVisible == true || _previewFocusUnavailable,
                "photo focus uses blur when supported and a low-resolution fallback otherwise");
            await Task.Delay(200); await CaptureFeatureSnapshotAsync("preview-focus-loading.png");
            ShowPreviewAt(122);
            await WaitFor(() => ReferenceEquals(PreviewSoftImage.Source, view[122].ThumbnailSource));
            _previewTestDecodeGate = null; decodeGate.TrySetResult(true);
            await WaitFor(() => !_previewIsLoading);
            await WaitFor(() => _previewIndex == 122 && ReferenceEquals(_previewLastReady, PreviewImage.Source) && PreviewSoftImage.Source == null);
            Check(_previewIndex == 122 && ReferenceEquals(_previewLastReady, PreviewImage.Source) && PreviewSoftImage.Source == null,
                "rapid photo changes discard stale decodes and finish on the latest image");
            _settings.AnimationsEnabled = false; ShowPreviewAt(121); await WaitFor(() => !_previewIsLoading);
            Check(PreviewImage.Opacity == 1 && PreviewSoftLayer.Opacity == 0, "disabled animations show a decoded photo immediately");
            _settings.AnimationsEnabled = true;
            var corruptIndex = view.FindIndex(p => p.FilePath.EndsWith("test-999-corrupt.jpg", StringComparison.OrdinalIgnoreCase));
            ShowPreviewAt(corruptIndex); await WaitFor(() => !_previewIsLoading);
            Check(PreviewReadError.Visibility == Visibility.Visible && _previewLastReady == null,
                "failed image decode shows an error rather than a stale photo or endless focus state");
            for(var index=0;index<400;index+=40)
            {
                ShowPreviewAt(index); await WaitFor(()=>!_previewIsLoading);
                await EnsureFilmstripThumbnailAsync(view[index],_filmstripCts!.Token); await Task.Delay(75);
                Check(_allPhotos.Count(p=>p.ThumbnailSource!=null)<=160,"filmstrip jump "+index+" releases recycled protection");
            }
            PreviewZoomIn_Click(this,new RoutedEventArgs()); await Task.Delay(250);
            Check(PreviewImageScroll.ZoomFactor>1,"detail zoom remains functional");
            ShowPreviewAt(121); await WaitFor(()=>!_previewIsLoading); await Task.Delay(450);
            Check(PreviewImage.Source is BitmapImage b&&b.PixelWidth>0,"detail image switch finishes decoding");
            Check(PreviewContentRoot.Opacity>0.95,"detail transition restores visible content");
            ApplyFitPreviewZoom();
            await WaitFor(() => Math.Abs(PreviewImageScroll.ZoomFactor - 1) < .01 && PreviewImageScroll.HorizontalOffset == 0);
            ZoomPreviewAt(new Windows.Foundation.Point(PreviewImageScroll.ActualWidth / 2, PreviewImageScroll.ActualHeight / 2), 2);
            await WaitFor(() => PreviewImageScroll.ZoomFactor >= 1.95 && PreviewImageScroll.HorizontalOffset > 0);
            Check(PreviewImageScroll.ZoomFactor >= 1.95 && PreviewImageScroll.HorizontalOffset > 0,"pointer centered wheel handler zooms and positions actual viewport");
            var previousX=PreviewImageScroll.HorizontalOffset;
            _previewDragStart=new Windows.Foundation.Point(300,200); _previewDragX=previousX; _previewDragY=PreviewImageScroll.VerticalOffset;
            PanPreviewTo(new Windows.Foundation.Point(240,180)); await Task.Delay(100);
            Check(PreviewImageScroll.HorizontalOffset>previousX,"drag handler pans a zoomed photo");
            ZoomPreviewAt(new Windows.Foundation.Point(100,100),.01f);
            await WaitFor(() => Math.Abs(PreviewImageScroll.ZoomFactor - 1) < .01 && PreviewImageScroll.HorizontalOffset == 0);
            Check(Math.Abs(PreviewImageScroll.ZoomFactor-1)<.01&&PreviewImageScroll.HorizontalOffset==0,"wheel zoom out returns to fit without stale offset");
            _filmstripHidden=false; ApplyFilmstripVisibility();
            PreviewFilmstripToggle_Click(this,new RoutedEventArgs());
            Check(PreviewFilmstripBar.Visibility==Visibility.Collapsed&&PreviewImageStage.Margin.Bottom==20,"hiding filmstrip frees photo space");
            PreviewFilmstripToggle_Click(this,new RoutedEventArgs()); await Task.Delay(100);
            Check(PreviewFilmstripBar.Visibility==Visibility.Visible,"filmstrip restores after explicit toggle");
            var presenter=AppWindow.Presenter;
            SetPreviewFullscreen(true); await Task.Delay(150);
            Check(AppWindow.Presenter.Kind==Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen&&DetailPanel.Visibility==Visibility.Collapsed&&PreviewFullscreenExit.Visibility==Visibility.Visible,"fullscreen hides information and filmstrip with visible exit");
            await CaptureFeatureSnapshotAsync("fullscreen.png");
            SetPreviewFullscreen(false); await Task.Delay(150);
            Check(AppWindow.Presenter.Kind==presenter.Kind&&PreviewToolbar.Visibility==Visibility.Visible&&DetailPanel.Visibility==Visibility.Visible,"exiting fullscreen restores previous presenter and information panel");
            ClosePreview(); await Task.Delay(450);

            EnterBatchMode();
            var favoriteButton = FindVisual<Button>(PhotoGrid).First(b => ReferenceEquals(b.DataContext, view[0]) && b.Tag == null);
            var wasFavorite = view[0].IsFavorite; CardFavorite_Click(favoriteButton, new RoutedEventArgs());
            Check(view[0].IsFavorite != wasFavorite, "completion feedback keeps favorite action functional");
            CardFavorite_Click(favoriteButton, new RoutedEventArgs());
            var selectionButton = FindVisual<Button>(PhotoGrid).First(b => b.Tag is string path && path == view[0].FilePath);
            SelectToggle_Click(selectionButton, new RoutedEventArgs());
            Check(view[0].IsSelected && _selectedPaths.Contains(view[0].FilePath) && SelectedCountText.Text.Contains("1"),
                "photo check and selection count update together");
            SelectToggle_Click(selectionButton, new RoutedEventArgs());
            Check(!view[0].IsSelected && !_selectedPaths.Contains(view[0].FilePath), "second check restores the selection state");
            _selectedPaths.Add(view[0].FilePath); _selectedPaths.Add(view[120].FilePath);
            Check(RemoveSelectedBtn.Visibility==Visibility.Visible&&_statusBarExpanded,"batch removal has a visible direct action");
            var total=_allPhotos.Count; var originalBytes=await File.ReadAllBytesAsync(view[0].FilePath);
            var cancelRemoval=RemoveSelectedAsync();
            await Task.Delay(100); await RemoveSelectedAsync();
            Check(_allPhotos.Count==total&&_removalDialogOpen,"repeated removal request cannot open overlapping dialogs");
            await InvokeDialogActionAsync("取消"); await cancelRemoval;
            Check(_allPhotos.Count==total,"canceling batch remove leaves the library untouched");
            var remove=RemoveSelectedAsync(); await InvokeDialogActionAsync("移除"); await remove;
            Check(_allPhotos.Count==total-2&&File.Exists(view[0].FilePath)&&(await File.ReadAllBytesAsync(view[0].FilePath)).SequenceEqual(originalBytes),"cross page batch removal preserves original photo bytes");
            Undo_Click(this,new RoutedEventArgs()); ExitBatchMode();
            Check(_allPhotos.Count==total&&_allPhotos.Select(p=>p.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()==total,"batch remove undo restores records without duplicates");
            var category=view[0].Category; ApplyClassification(new(){{view[0],"规则测试"}});
            Check(view[0].Category=="规则测试","rule classification applies preview proposal"); Undo_Click(this,new RoutedEventArgs());
            Check(view[0].Category==category,"rule classification undo restores manual category");

            SaveMapPlacement(new[]{view[0],view[120],view[400]},31.2,121.5,"qa-place","测试地点","地图批注 <script> & 本地文本");
            await LibraryStore.SaveAsync(_allPhotos); var restored=await LibraryStore.LoadAsync();
            Check(restored.Photos.Count(p=>p.MapPlaceId=="qa-place")==3&&restored.Photos.First(p=>p.MapPlaceId=="qa-place").MapNote.Contains("<script>"),"map placement and annotation persist as plain local metadata");
            _mapLatitude=31.2; _mapLongitude=121.5; OpenMap(); await Task.Delay(250);
            await RenderMapAsync();
            foreach (var width in new[] { 1100d, 700d, 480d })
            {
                MapOverlay.Width = width; MapOverlay.UpdateLayout(); await Task.Delay(120);
                var heading = Bounds(MapHeading, MapHeader); var actions = Bounds(MapActionsScroll, MapHeader); var searchBounds = Bounds(MapSearchLayout, MapHeader);
                var separated = heading.Right <= actions.Left + 1 || actions.Right <= heading.Left + 1 || heading.Bottom <= actions.Top + 1 || actions.Bottom <= heading.Top + 1;
                Check(separated && heading.Left >= 0 && actions.Right <= MapHeader.ActualWidth + 1 && searchBounds.Right <= MapHeader.ActualWidth + 1 &&
                      searchBounds.Top >= Math.Max(heading.Bottom, actions.Bottom) - 1 && Math.Abs(searchBounds.Left - heading.Left) < 1,
                      $"map heading, actions and search align without overlap at {width:F0}px");
                if (width == 480) await CaptureFeatureSnapshotAsync("map-compact-alignment.png");
            }
            MapOverlay.Width = double.NaN; MapOverlay.UpdateLayout(); await RenderMapAsync();
            Check(MapMarkers.Children.Count==1,"photos at one point render as one stacked marker");
            var tileViews=_mapTileViews.Values.ToArray(); var marker=MapMarkers.Children[0];
            var groups=_mapGroups;
            await RenderMapAsync();
            Check(tileViews.All(_mapTileViews.Values.Contains)&&ReferenceEquals(marker,MapMarkers.Children[0])&&ReferenceEquals(groups,_mapGroups),"stationary map reuses tile images, marker controls and grouped locations");
            var mapRenderWatch=Stopwatch.StartNew();
            for(var i=0;i<20;i++){_mapLongitude+=.001;await RenderMapAsync();}
            mapRenderWatch.Stop();
            Check(_mapTileViews.Count<=80&&ReferenceEquals(marker,MapMarkers.Children[0]),"small pans keep map layers bounded without rebuilding marker controls");
            var initialMapZoom=_mapZoom;
            ChangeMapZoom(1,new Windows.Foundation.Point(600,250)); ChangeMapZoom(1,new Windows.Foundation.Point(600,250));
            await WaitFor(()=>!_mapAnimating);
            Check(_mapZoom==initialMapZoom+2&&_mapRenderedZoom==_mapZoom,"rapid wheel steps interrupt and retarget compositor zoom without dropping input");
            Check(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MapTiles).Scale.X==1,"finished compositor motion resets scale for accurate map hit testing");
            await CaptureFeatureSnapshotAsync("map-stack.png");
            var choose=ChooseMapPhotosAsync();
            await WaitFor(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Any(p => FindVisual<ContentDialog>(p.Child).Any()));
            var picker=OpenTestDialog(); var list=FindVisual<ListView>((DependencyObject)picker.Content).Single();
            list.SelectedItems.Add(view[1]);
            var preview=FindVisual<Image>((DependencyObject)picker.Content).Single(i=>i.Name=="MapPickerPreviewImage");
            await WaitFor(()=>preview.Source is BitmapImage bmp&&bmp.PixelWidth>0);
            Check(picker.IsPrimaryButtonEnabled,"photo preview selection enables placement confirmation");
            Check(FindVisual<Image>(list).Any(i=>i.Source!=null),"map picker visible rows load real photo thumbnails");
            await CaptureFeatureSnapshotAsync("map-picker.png",picker);
            var search=FindVisual<TextBox>((DependencyObject)picker.Content).Single(); search.Text="no-such-photo"; await Task.Delay(75); search.Text=""; await Task.Delay(75);
            await InvokeDialogActionAsync("下一步"); var selected=await choose;
            Check(selected.Count==1&&ReferenceEquals(selected[0],view[1]),"map photo selection survives search filtering");
            BeginMapPlacement(selected);
            await WaitFor(()=>MapPlacementImage.Source is BitmapImage bmp&&bmp.PixelWidth>0);
            Check(MapPlacementPreview.Visibility==Visibility.Visible&&MapPlacementName.Text==view[1].Filename,"map placement keeps the chosen photo visible beside the map");
            MapOverlay.UpdateLayout();
            Check(Bounds(MapPlacementPreview, MapOverlay).Top >= Bounds(MapHeader, MapOverlay).Bottom,
                "floating map header leaves the placement photo preview unobscured");
            Check(MapScaleLine.Width > 0 && MapScaleLine.Width <= 100 && !string.IsNullOrWhiteSpace(MapScaleText.Text),
                "immersive map scale has a bounded distance based on the camera");
            await CaptureFeatureSnapshotAsync("map-placement-preview.png");
            BeginMapPlacement(new(){view[1],view[2]}); MapPlacementNext_Click(this,new RoutedEventArgs());
            await WaitFor(()=>MapPlacementName.Text==view[2].Filename&&MapPlacementImage.Source is BitmapImage bmp&&bmp.PixelWidth>0);
            Check(_mapPlacement.Count==2&&_mapPlacementPreviewIndex==1,"placement preview navigation preserves the whole selected batch");
            var place=PlacePhotosAsync(32.1,120.2);
            await WaitFor(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Any(p => FindVisual<ContentDialog>(p.Child).Any()));
            var placementDialog=OpenTestDialog(); var fields=FindVisual<TextBox>((DependencyObject)placementDialog.Content).ToArray();
            fields.First(b=>b.Header is string h&&h=="地点名称").Text="第二地点";
            fields.First(b=>b.AcceptsReturn).Text="新的本地批注";
            await InvokeDialogActionAsync("放在这里"); await place;
            Check(view[1].MapLatitude==32.1&&view[1].MapLongitude==120.2&&view[1].MapLabel=="第二地点"&&view[1].MapNote=="新的本地批注","photo placement dialog writes only confirmed coordinates and annotation");
            Check(MapPlacementPreview.Visibility==Visibility.Collapsed&&MapPlacementImage.Source==null,"confirmed placement releases preview and prevents stale photo updates");
            Undo_Click(this,new RoutedEventArgs()); await Task.Delay(100);
            Check(view[1].MapLatitude==null,"confirmed placement can be undone on map toolbar");
            var returnCamera = (Latitude: _mapLatitude, Longitude: _mapLongitude, Zoom: _mapZoom);
            foreach (var tile in _mapTileViews.Values) tile.Image.Source = view[0].ThumbnailSource;
            var previousDuration = _settings.AnimationDurationMs; _settings.AnimationDurationMs = 500;
            AnimateToMapPlace(new(){view[0],view[120],view[400]});
            Check(_mapZoom == 17 && Math.Abs(_mapLatitude - view[0].MapLatitude!.Value) < .00001 && Math.Abs(_mapLongitude - view[0].MapLongitude!.Value) < .00001,
                "opening a map location targets its exact coordinates at the maximum supported zoom");
            Check(_mapAnimating && (_mapBlurVisual?.IsVisible == true || _mapBlurUnavailable), "place flight uses dynamic map blur when supported and otherwise falls back safely");
            await CaptureFeatureSnapshotAsync("map-flight.png"); _settings.AnimationDurationMs = previousDuration;
            Check(MapPlaceOverlay.Visibility == Visibility.Visible && _mapPlaceEntranceActive,
                "place reveal starts with the camera flight instead of waiting for it to stop");
            await WaitFor(() => MapTransitionHero.Visibility == Visibility.Visible);
            Check(_mapPlaceEntranceActive && MapHeroImage.Source != null, "map stack uses a real thumbnail for its connected opening flight");
            await CaptureFeatureSnapshotAsync("map-transition-opening.png");
            await WaitFor(() => !_mapPlaceEntranceActive && !_mapAnimating);
            Check(_mapBlurVisual?.IsVisible != true, "map blur overlay is hidden after flight");
            Check(MapTransitionHero.Visibility == Visibility.Collapsed && MapHeroImage.Source == null &&
                  MapVisual(MapPlaceOverlay).Opacity > .99, "completed place entrance releases its temporary hero and restores visible content");
            Check(!_mapAnimating&&MapPlacePhotos.Items.Count==3&&MapPlaceNote.Text.Contains("<script>"),"map point zoom transition unfolds local photo cards and safe notes");
            await WaitFor(() => MapPlaceHeroBackdrop.Source != null);
            Check(MapPlaceHeroBackdrop.Opacity <= .15 && Grid.GetRowSpan(MapPlaceOverlay) == 2 &&
                  MapPlaceOverlay.Background is SolidColorBrush placeGlass && placeGlass.Color.A < 16,
                "place details preserve the map as a visible full-window spatial background");
            MapPlaceOverlay.Width = 480; MapPlaceOverlay.UpdateLayout(); await Task.Delay(120);
            var placeHeading = Bounds(MapPlaceHeading, MapPlaceHeader); var placeActions = Bounds(MapPlaceActionsScroll, MapPlaceHeader);
            Check(placeHeading.Bottom <= placeActions.Top + 1 && placeActions.Right <= MapPlaceHeader.ActualWidth + 1,
                "narrow place detail keeps its heading and actions on separate aligned rows");
            var firstPlaceCard = FindVisual<StackPanel>(MapPlacePhotos).First(p => p.Width == 174);
            Check(Bounds(MapPlacePhotos, MapPlaceOverlay).Top > Bounds(MapPlaceHeader, MapPlaceOverlay).Bottom &&
                  Bounds(firstPlaceCard, MapPlacePhotos).Left >= 0,
                "place photos form a separate horizontal bottom reel under the location context");
            await CaptureFeatureSnapshotAsync("map-place-compact-alignment.png");
            MapPlaceOverlay.Width = double.NaN; MapPlaceOverlay.UpdateLayout();
            await CaptureFeatureSnapshotAsync("map-place.png");
            MapPlaceClose_Click(this, new RoutedEventArgs());
            await WaitFor(() => MapTransitionHero.Visibility == Visibility.Visible && _mapPlaceClosing);
            await CaptureFeatureSnapshotAsync("map-transition-closing.png");
            await WaitFor(() => MapPlaceOverlay.Visibility == Visibility.Collapsed && !_mapPlaceClosing && !_mapAnimating);
            Check(MapPlaceHeroBackdrop.Source == null, "closing the place releases its photo backdrop");
            Check(Math.Abs(_mapLatitude - returnCamera.Latitude) < .00001 &&
                  Math.Abs(_mapLongitude - returnCamera.Longitude) < .00001 && _mapZoom == returnCamera.Zoom,
                  "return from a place smoothly restores the previous map camera");
            _settings.AnimationsEnabled = false;
            AnimateToMapPlace(new(){view[0],view[120],view[400]});
            await WaitFor(() => MapPlaceOverlay.Visibility == Visibility.Visible);
            Check(!_mapPlaceEntranceActive && MapTransitionHero.Visibility == Visibility.Collapsed && MapVisual(MapPlaceOverlay).Opacity > .99,
                  "disabled animations show place photos immediately without a hidden or flying layer");
            MapPlaceClose_Click(this, new RoutedEventArgs());
            await WaitFor(() => MapPlaceOverlay.Visibility == Visibility.Collapsed && !_mapPlaceClosing);
            _settings.AnimationsEnabled = true;
            AnimateToMapPlace(new(){view[0],view[120],view[400]});
            await WaitFor(() => MapPlaceOverlay.Visibility == Visibility.Visible && !_mapPlaceEntranceActive);
            SaveMapPlacement(_mapPlace,31.2,121.5,"qa-place","修改后标题","修改后批注"); ShowMapPlace(_mapPlace);
            Undo_Click(this,new RoutedEventArgs()); await Task.Delay(100);
            Check(MapPlaceTitle.Text.Contains("测试地点")&&MapPlaceNote.Text.Contains("本地文本"),"map annotation undo refreshes visible title and notes");
            var originalView=_currentView;
            _mapPhotoPreviewSource=CaptureMapPhotoPreviewSource(_mapPlace[0]);
            _mapPreviewRestore=_currentView; _currentView=_mapPlace.ToList(); OpenPreview(_mapPlace[0]);
            await WaitFor(()=>!_previewIsLoading);
            Check(_currentView.Count==3,"map photo preview is scoped to the chosen location");
            Check(_mapPhotoPreviewSource.HasValue && PreviewOverlay.Background is SolidColorBrush previewShade && previewShade.Color.A < 200,
                  "map photo opens from its card over a dimmed but still visible location scene");
            Check(MapPhotoHeroLayer.Visibility==Visibility.Visible&&_mapPhotoPreviewSourceElement!=null&&MapVisual(_mapPhotoPreviewSourceElement).Opacity<.01,
                  "map photo entrance uses a single moving thumbnail while concealing the duplicate source card");
            await WaitFor(()=>MapPhotoHeroLayer.Visibility==Visibility.Collapsed&&MapVisual(PreviewImageStage).Opacity>.99);
            ClosePreview(); await WaitFor(() => ReferenceEquals(_currentView, originalView));
            Check(ReferenceEquals(_currentView,originalView),"closing map detail restores gallery query results");
            SaveMapPlacement(_mapPlace,null,null,"","","");
            Check(_mapPlace.All(p=>p.MapLatitude==null)&&_allPhotos.Count==total,"removing a map location preserves library records");
            Undo_Click(this,new RoutedEventArgs()); await Task.Delay(100);
            Check(_mapPlace.All(p=>p.MapLatitude==31.2),"map removal undo restores coordinates and notes");
            MapPlaceClose_Click(this, new RoutedEventArgs());
            MapClose_Click(this,new RoutedEventArgs());
            Check(MapOverlay.Visibility==Visibility.Collapsed&&_mapThumbnailLifetime.IsCancellationRequested&&
                  MapTransitionHero.Visibility==Visibility.Collapsed&&!_mapPlaceEntranceActive&&!_mapPlaceClosing,
                  "closing map cancels pending previews and every connected transition layer");

            OpenComparison(new[] {view[0],view[1]},null);
            await WaitFor(()=>_comparePanes.Count==2&&_comparePanes.All(p=>p.Image.Source is BitmapImage b&&b.PixelWidth>0));
            Check(_comparePanes.Count==2,"comparison still decodes both photos");
            CompareClose_Click(this,new RoutedEventArgs());
            foreach(var theme in new[] {ElementTheme.Light,ElementTheme.Dark})
            {
                var settings=_settings.Clone(); settings.ThemeMode=theme==ElementTheme.Dark?1:0;
                var editor=new SettingsEditor(settings,_allPhotos,WinRT.Interop.WindowNative.GetWindowHandle(this),760,450);
                var dialog=new ContentDialog {Content=editor,CloseButtonText="关闭",XamlRoot=Content.XamlRoot,RequestedTheme=theme};
                dialog.Resources["ContentDialogMaxWidth"]=850d;
                var shown=dialog.ShowAsync(); await Task.Delay(350);
                Check(editor.ActualTheme==theme,"settings applies "+theme+" theme");
                await CaptureFeatureSnapshotAsync(theme == ElementTheme.Dark ? "settings-dark.png" : "settings-light.png", dialog);
                var settingsNavigation = FindVisual<ListView>(editor).First();
                settingsNavigation.SelectedIndex = 1; await Task.Delay(80);
                var modes = FindVisual<CommunityToolkit.WinUI.Controls.Segmented>(editor).First();
                var savedMode = settings.DisplayMode;
                modes.SelectedIndex = 1 - savedMode;
                Check(editor.Draft.DisplayMode == 1 - savedMode && settings.DisplayMode == savedMode,
                    "toolkit layout selector edits only the settings draft in " + theme);
                Check(modes.ActualWidth > 0 && modes.ActualHeight >= 36,
                    "toolkit layout selector is rendered with usable targets in " + theme);
                await CaptureFeatureSnapshotAsync(theme == ElementTheme.Dark ? "settings-layout-dark.png" : "settings-layout-light.png", dialog);
                settingsNavigation.SelectedIndex = 0;
                var clear=FindButtons(editor).FirstOrDefault(b=>b.Content is string label&&label=="清理缩略图缓存");
                Check(clear!=null&&clear.CornerRadius.TopLeft==12&&clear.MinHeight==36,"settings cache action uses unified button style in "+theme);
                var close=FindButtons(dialog).FirstOrDefault(b=>b.Content is string label&&label=="关闭");
                Check(close!=null&&close.CornerRadius.TopLeft==12,"dialog action matches shared button radius in "+theme);
                dialog.Hide(); await shown; editor.CancelPendingOperations();
            }
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory,"performance-smoke.json"),JsonSerializer.Serialize(new {success=true,checks,seconds=watch.Elapsed.TotalSeconds,
                photos=_allPhotos.Count,residentThumbnails=_allPhotos.Count(p=>p.ThumbnailSource!=null),workingSetMB=Process.GetCurrentProcess().WorkingSet64/1048576d,
                mapRender20SmallPansMs=mapRenderWatch.Elapsed.TotalMilliseconds,mapTileViews=_mapTileViews.Count,initialRealizedCards,
                mapBlurAvailable=!_mapBlurUnavailable,mapBlurFailure=_mapBlurFailure}));
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory,"performance-smoke.json"),JsonSerializer.Serialize(new {success=false,checks,error=ex.ToString()}));
        }
        finally { Close(); }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        if(root is Button button) yield return button;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in FindButtons(VisualTreeHelper.GetChild(root,i))) yield return child;
    }

    private async Task CaptureFeatureSnapshotAsync(string name, UIElement? target=null)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(target??(UIElement)Content);
        var pixels = await bitmap.GetPixelsAsync();
        using var output = File.Create(Path.Combine(AppPaths.DataDirectory,name));
        using var stream = output.AsRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());
        await encoder.FlushAsync();
    }

    private ContentDialog OpenTestDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot)
        .SelectMany(p=>FindVisual<ContentDialog>(p.Child)).First();

    private async Task InvokeDialogActionAsync(string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        Button? button = null;
        while (button == null)
        {
            button = VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot)
                .SelectMany(p => FindVisual<ContentDialog>(p.Child)).SelectMany(FindButtons)
                .FirstOrDefault(b => b.IsLoaded && b.IsEnabled && b.Content is string text && text == label);
            if (button != null) break;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Dialog action did not become ready: {label}");
            await Task.Delay(50);
        }
        var peer=Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button);
        var invoke=(Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
        invoke.Invoke();
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if(root is T value) yield return value;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in FindVisual<T>(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
}
