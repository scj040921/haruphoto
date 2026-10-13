using System.Globalization;

namespace PhotoAlbum;

internal sealed record GalleryFilter(bool Favorites, string Category, string Month, int MinRating,
    int Days, string Organize, string Search, int Sort);

internal static class GalleryQuery
{
    public static List<PhotoItem> Apply(IEnumerable<PhotoItem> photos, GalleryFilter filter)
    {
        var hasMonth = !string.IsNullOrEmpty(filter.Month);
        var validMonth = DateTime.TryParseExact(filter.Month, "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var month);
        var cutoff = DateTime.Now.AddDays(-Math.Max(0, filter.Days));
        var hasSearch = !string.IsNullOrWhiteSpace(filter.Search);
        var query = photos.Where(p =>
            (!filter.Favorites || p.IsFavorite) &&
            (filter.Category.Length == 0 || (filter.Category == "__UNCATEGORIZED__" ? p.Category.Length == 0 : p.Category == filter.Category)) &&
            (!hasMonth || (validMonth && p.TimelineDate.Year == month.Year && p.TimelineDate.Month == month.Month)) &&
            p.Rating >= filter.MinRating &&
            (filter.Days == 0 || (filter.Days < 0 ? !p.DateTaken.HasValue : p.DateAdded >= cutoff)) &&
            (filter.Organize != "FavUnrated" || (p.IsFavorite && p.Rating == 0)) &&
            (filter.Organize != "Unfavorite" || !p.IsFavorite) &&
            (!hasSearch || p.Filename.Contains(filter.Search, StringComparison.OrdinalIgnoreCase)
                || p.Category.Contains(filter.Search, StringComparison.OrdinalIgnoreCase)
                || p.DateText.Contains(filter.Search, StringComparison.OrdinalIgnoreCase)));
        return (filter.Sort switch
        {
            1 => query.OrderBy(p => p.Filename, StringComparer.CurrentCultureIgnoreCase),
            2 => query.OrderByDescending(p => p.Rating).ThenByDescending(p => p.DateAdded),
            _ => query.OrderByDescending(p => p.DateAdded),
        }).ToList();
    }

    public static List<PhotoItem> Page(List<PhotoItem> view, int index, int size)
    {
        var offset = Math.Clamp(index, 0, Math.Max(0, (view.Count - 1) / size)) * size;
        return view.GetRange(offset, Math.Min(size, view.Count - offset));
    }
}
