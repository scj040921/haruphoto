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
            var view = _currentView;
            for(var page=0;page<=(_currentView.Count-1)/PageSize;page++)
            {
                _currentPage=page; RenderCurrentPage(); await _thumbnailBatchTask;
                Check(ReferenceEquals(view,_currentView), "page "+page+" reuses sorted view");
                Check(_allPhotos.Count(p=>p.ThumbnailSource!=null)<=256,"page "+page+" obeys thumbnail memory bound");
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
            Check(FindVisual<Border>(PhotoGrid).Where(b=>b.Name=="CardRoot").All(b=>b.CornerRadius.TopLeft==0),"switching back to masonry restores square joined cards");

            OpenPreview(view[120]);
            await WaitFor(()=>PreviewLoading.Visibility!=Visibility.Visible);
            await EnsureFilmstripThumbnailAsync(view[120],_filmstripCts!.Token);
            Check(view[120].ThumbnailSource!=null,"filmstrip loads a photo outside current gallery page");
            for(var index=0;index<400;index+=40)
            {
                ShowPreviewAt(index); await WaitFor(()=>PreviewLoading.Visibility!=Visibility.Visible);
                await EnsureFilmstripThumbnailAsync(view[index],_filmstripCts!.Token); await Task.Delay(75);
                Check(_allPhotos.Count(p=>p.ThumbnailSource!=null)<=256,"filmstrip jump "+index+" releases recycled protection");
            }
            PreviewZoomIn_Click(this,new RoutedEventArgs()); await Task.Delay(250);
            Check(PreviewImageScroll.ZoomFactor>1,"detail zoom remains functional");
            ShowPreviewAt(121); await WaitFor(()=>PreviewLoading.Visibility!=Visibility.Visible); await Task.Delay(450);
            Check(PreviewImage.Source is BitmapImage b&&b.PixelWidth>0,"detail image switch finishes decoding");
            Check(PreviewContentRoot.Opacity>0.95,"detail transition restores visible content");
            ApplyFitPreviewZoom(); await Task.Delay(150);
            ZoomPreviewAt(new Windows.Foundation.Point(PreviewImageScroll.ActualWidth / 2, PreviewImageScroll.ActualHeight / 2), 2);
            await Task.Delay(150);
            Check(PreviewImageScroll.ZoomFactor >= 1.95 && PreviewImageScroll.HorizontalOffset > 0,"pointer centered wheel handler zooms and positions actual viewport");
            var previousX=PreviewImageScroll.HorizontalOffset;
            _previewDragStart=new Windows.Foundation.Point(300,200); _previewDragX=previousX; _previewDragY=PreviewImageScroll.VerticalOffset;
            PanPreviewTo(new Windows.Foundation.Point(240,180)); await Task.Delay(100);
            Check(PreviewImageScroll.HorizontalOffset>previousX,"drag handler pans a zoomed photo");
            ZoomPreviewAt(new Windows.Foundation.Point(100,100),.01f); await Task.Delay(100);
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

            EnterBatchMode(); _selectedPaths.Add(view[0].FilePath); _selectedPaths.Add(view[120].FilePath);
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
            Check(MapMarkers.Children.Count==1,"photos at one point render as one stacked marker");
            await CaptureFeatureSnapshotAsync("map-stack.png");
            var choose=ChooseMapPhotosAsync(); await Task.Delay(150);
            var picker=OpenTestDialog(); var list=FindVisual<ListView>((DependencyObject)picker.Content).Single();
            list.SelectedItems.Add(view[1]);
            var search=FindVisual<TextBox>((DependencyObject)picker.Content).Single(); search.Text="no-such-photo"; await Task.Delay(75); search.Text=""; await Task.Delay(75);
            await InvokeDialogActionAsync("下一步"); var selected=await choose;
            Check(selected.Count==1&&ReferenceEquals(selected[0],view[1]),"map photo selection survives search filtering");
            _mapPlacement=selected; var place=PlacePhotosAsync(32.1,120.2); await Task.Delay(100);
            var placementDialog=OpenTestDialog(); var fields=FindVisual<TextBox>((DependencyObject)placementDialog.Content).ToArray();
            fields.First(b=>b.Header is string h&&h=="地点名称").Text="第二地点";
            fields.First(b=>b.AcceptsReturn).Text="新的本地批注";
            await InvokeDialogActionAsync("放在这里"); await place;
            Check(view[1].MapLabel=="第二地点"&&view[1].MapNote=="新的本地批注"&&MapMarkers.Children.Count==2,"photo placement dialog writes only confirmed coordinates and annotation");
            Undo_Click(this,new RoutedEventArgs()); await Task.Delay(100);
            Check(view[1].MapLatitude==null,"confirmed placement can be undone on map toolbar");
            AnimateToMapPlace(new(){view[0],view[120],view[400]}); await WaitFor(()=>MapPlaceOverlay.Visibility==Visibility.Visible);
            await Task.Delay(400);
            Check(!_mapAnimating&&MapPlacePhotos.Items.Count==3&&MapPlaceNote.Text.Contains("<script>"),"map point zoom transition unfolds local photo cards and safe notes");
            await CaptureFeatureSnapshotAsync("map-place.png");
            SaveMapPlacement(_mapPlace,31.2,121.5,"qa-place","修改后标题","修改后批注"); ShowMapPlace(_mapPlace);
            Undo_Click(this,new RoutedEventArgs()); await Task.Delay(100);
            Check(MapPlaceTitle.Text.Contains("测试地点")&&MapPlaceNote.Text.Contains("本地文本"),"map annotation undo refreshes visible title and notes");
            var originalView=_currentView;
            _mapPreviewRestore=_currentView; _currentView=_mapPlace.ToList(); OpenPreview(_mapPlace[0]);
            await WaitFor(()=>PreviewLoading.Visibility!=Visibility.Visible);
            Check(_currentView.Count==3,"map photo preview is scoped to the chosen location");
            ClosePreview(); await Task.Delay(400);
            Check(ReferenceEquals(_currentView,originalView),"closing map detail restores gallery query results");
            SaveMapPlacement(_mapPlace,null,null,"","","");
            Check(_mapPlace.All(p=>p.MapLatitude==null)&&_allPhotos.Count==total,"removing a map location preserves library records");
            Undo_Click(this,new RoutedEventArgs()); await Task.Delay(100);
            Check(_mapPlace.All(p=>p.MapLatitude==31.2),"map removal undo restores coordinates and notes");
            MapClose_Click(this,new RoutedEventArgs());
            Check(MapOverlay.Visibility==Visibility.Collapsed&&_mapThumbnailLifetime.IsCancellationRequested,"closing map cancels pending preview work");

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
                var clear=FindButtons(editor).FirstOrDefault(b=>b.Content is string label&&label=="清理缩略图缓存");
                Check(clear!=null&&clear.CornerRadius.TopLeft==12&&clear.MinHeight==36,"settings cache action uses unified button style in "+theme);
                var close=FindButtons(dialog).FirstOrDefault(b=>b.Content is string label&&label=="关闭");
                Check(close!=null&&close.CornerRadius.TopLeft==12,"dialog action matches shared button radius in "+theme);
                dialog.Hide(); await shown; editor.CancelPendingOperations();
            }
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory,"performance-smoke.json"),JsonSerializer.Serialize(new {success=true,checks,seconds=watch.Elapsed.TotalSeconds,
                photos=_allPhotos.Count,residentThumbnails=_allPhotos.Count(p=>p.ThumbnailSource!=null),workingSetMB=Process.GetCurrentProcess().WorkingSet64/1048576d}));
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

    private async Task CaptureFeatureSnapshotAsync(string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync((UIElement)Content);
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
        await Task.Delay(150);
        var dialog=OpenTestDialog();
        var button=FindButtons(dialog).First(b=>b.Content is string text&&text==label);
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
