using System.Globalization;
using System.Text.Json;

namespace PhotoAlbum;

internal sealed class MapSearchResult
{
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

internal static class MapSearchParser
{
    public static List<MapSearchResult> Parse(JsonElement root)
    {
        var results = new List<MapSearchResult>();
        if (root.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("lat", out var latJson) || !TryCoordinate(latJson, out var latitude)
                || !item.TryGetProperty("lon", out var lonJson) || !TryCoordinate(lonJson, out var longitude)
                || !MapGeometry.Valid(latitude, longitude)) continue;
            var address = item.TryGetProperty("display_name", out var displayJson) && displayJson.ValueKind == JsonValueKind.String ? displayJson.GetString()?.Trim() ?? "" : "";
            var localizedName = item.TryGetProperty("name", out var nameJson) && nameJson.ValueKind == JsonValueKind.String ? nameJson.GetString()?.Trim() : null;
            var name = string.IsNullOrWhiteSpace(localizedName) ? address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "未命名地点" : localizedName;
            results.Add(new MapSearchResult { Name = name, Address = address, Latitude = latitude, Longitude = longitude });
        }
        return results;
    }

    private static bool TryCoordinate(JsonElement value, out double coordinate)
    {
        coordinate = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out coordinate);
        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate);
    }
}
