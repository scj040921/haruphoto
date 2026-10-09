using System.Security.Cryptography;
using System.Text;
namespace PhotoAlbum;
internal static class AppPaths
{
    private static readonly string? TestRoot = GetTestRoot();
    public static bool IsTestProfile => TestRoot != null;
    public static string DataDirectory => TestRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "haruphoto");
    public static string InstanceName => TestRoot == null ? "haruphoto_single_instance" : "haruphoto_test_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TestRoot)))[..16];
    private static string? GetTestRoot()
    {
        var value = Environment.GetEnvironmentVariable("HARUPHOTO_TEST_DATA");
        if (string.IsNullOrWhiteSpace(value)) return null;
        var full = Path.GetFullPath(value);
        var prefix = Path.Combine(Path.GetTempPath(), "haruphoto-ui-test-");
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(full[prefix.Length..], "N", out _)) throw new InvalidOperationException("Invalid isolated test profile.");
        return full;
    }
}
