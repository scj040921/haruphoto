namespace PhotoAlbum;

internal sealed record PhotoFile(string Path, long Size, DateTime Modified);

internal static class PhotoFileScanner
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff" };

    public static List<PhotoFile> Scan(IEnumerable<string> inputs, IEnumerable<string> known, CancellationToken token = default)
    {
        var seen = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var found = new List<PhotoFile>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (var input in inputs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var files = Directory.Exists(input) ? Directory.EnumerateFiles(input, "*", options) : new[] { input };
                foreach (var path in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Extensions.Contains(Path.GetExtension(path)) || seen.Contains(path)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists && seen.Add(info.FullName)) found.Add(new PhotoFile(info.FullName, info.Length, info.LastWriteTime));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }
}
