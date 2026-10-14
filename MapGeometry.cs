namespace PhotoAlbum;

internal static class MapGeometry
{
    public const double MaxLatitude = 85.05112878;
    public static bool Valid(double? latitude, double? longitude) => latitude is >= -90 and <= 90
        && longitude is >= -180 and <= 180;
    public static (double X, double Y) Project(double latitude, double longitude, double zoom)
    {
        var world = 256 * Math.Pow(2, zoom);
        var sin = Math.Sin(Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180);
        return ((longitude + 180) / 360 * world, (0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI)) * world);
    }
    public static (double Latitude, double Longitude) Unproject(double x, double y, double zoom)
    {
        var world = 256 * Math.Pow(2, zoom);
        x = ((x % world) + world) % world;
        y = Math.Clamp(y, 0, world);
        return (180 / Math.PI * Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / world))), x / world * 360 - 180);
    }
    public static double WrappedDelta(double x, double center, double zoom)
    {
        var world = 256 * Math.Pow(2, zoom);
        return ((x - center + world / 2) % world + world) % world - world / 2;
    }
}
