using Microsoft.UI.Xaml;

namespace PhotoAlbum;

/// <summary>定时触发后台扫描；取消旧扫描并避免同一轮扫描重叠。</summary>
public sealed class FolderWatcherService
{
    private readonly DispatcherTimer _timer = new();
    private CancellationTokenSource? _lifetime;
    private string[] _folders = Array.Empty<string>();
    private bool _scanning;
    public Func<IEnumerable<string>> KnownPaths { get; set; } = () => Array.Empty<string>();
    internal event Action<List<PhotoFile>>? NewFilesFound;

    public FolderWatcherService() => _timer.Tick += OnTick;

    public void Start(int intervalMinutes, List<string> watchedFolders)
    {
        Stop();
        _folders = watchedFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_folders.Length == 0) return;
        _lifetime = new CancellationTokenSource();
        _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private async void OnTick(object? sender, object e)
    {
        if (_scanning || _lifetime == null) return;
        _scanning = true;
        var token = _lifetime.Token;
        var folders = _folders;
        var known = KnownPaths().ToArray();
        try
        {
            var found = await Task.Run(() => PhotoFileScanner.Scan(folders, known, token), token);
            if (!token.IsCancellationRequested && found.Count > 0) NewFilesFound?.Invoke(found);
        }
        catch (OperationCanceledException) { }
        catch { /* 不可访问的目录将在下次扫描时重试。 */ }
        finally { _scanning = false; }
    }
}
