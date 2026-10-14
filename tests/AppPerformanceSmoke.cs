// 仅 PerformanceSmoke=true 的 QA 构建编译此文件，正式程序不包含自动操作。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Text.Json;

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
            ClosePreview(); await Task.Delay(450);

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

    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if(root is T value) yield return value;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in FindVisual<T>(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
}
