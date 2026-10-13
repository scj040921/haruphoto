using System.Text.Json;

namespace PhotoAlbum;

internal static class UpdateStartup
{
    internal static bool IsOwnedSignal(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var prefix = Path.Combine(Path.GetTempPath(), "haruphoto-update-");
        return Path.GetFileName(full) == "restarted.json" && directory.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(directory[prefix.Length..],"N",out _);
    }
    public static void SignalReady()
    {
        var path = Environment.GetEnvironmentVariable("HARUPHOTO_UPDATE_READY_FILE");
        Environment.SetEnvironmentVariable("HARUPHOTO_UPDATE_READY_FILE",null);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!IsOwnedSignal(path)) return;
            File.WriteAllText(path,JsonSerializer.Serialize(new {pid=Environment.ProcessId,version=UpdateService.CurrentVersion,channel=UpdateService.CurrentBuild.Channel,tag=UpdateService.CurrentBuild.Tag}));
        }
        catch { /* 更新器没有收到信号时会保留备份并回滚。 */ }
    }
}
