using System.Text.Json;

namespace PhotoAlbum;

/// <summary>由打包流程写入，不使用用户设置冒充当前已安装的发行通道。</summary>
public sealed record BuildIdentity(string Version, string Channel, string Tag, string Commit)
{
    public static BuildIdentity Load(string directory, string version)
    {
        try
        {
            var build = JsonSerializer.Deserialize<BuildIdentity>(File.ReadAllText(Path.Combine(directory, "build-info.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (build != null && build.Version == version && build.Channel is "Stable" or "Nightly"
                && UpdateService.IsChannelTag(build.Tag, build.Channel)) return build;
        }
        catch { /* 老安装包没有构建标识，视为 Stable。 */ }
        return new(version, "Stable", "v" + version, "");
    }
}
