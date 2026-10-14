using PhotoAlbum;
using System.Text.Json;

internal static class FeatureChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var zoom = ViewerGeometry.ZoomAt(100, 50, 2, 240, 160, 1.5f, 800, 600);
        check(zoom.Zoom == 3 && zoom.X == 270 && zoom.Y == 155, "wheel zoom preserves the pixel below the pointer");
        var reset = ViewerGeometry.ZoomAt(500, 500, 2, 120, 100, .1f, 800, 600);
        check(reset.Zoom == 1 && reset.X == 0 && reset.Y == 0, "zoom out clamps to fit and resets scroll offsets");
        check(ViewerGeometry.ZoomAt(0, 0, 7, 100, 100, 3, 800, 600).Zoom == 8, "zoom upper bound remains eight times");
        foreach (var point in new[] { (0d, 0d), (35.2, 104.3), (-80d, -179.9), (80d, 179.9) })
        {
            var projected = MapGeometry.Project(point.Item1, point.Item2, 10);
            var back = MapGeometry.Unproject(projected.X, projected.Y, 10);
            check(Math.Abs(point.Item1 - back.Latitude) < .00001 && Math.Abs(point.Item2 - back.Longitude) < .00001, "map projection roundtrip " + point);
        }
        check(!MapGeometry.Valid(double.NaN, 20) && !MapGeometry.Valid(40, 181) && !MapGeometry.Valid(null, null), "invalid and absent map positions rejected");
        var west = MapGeometry.Project(0, -179, 5); var east = MapGeometry.Project(0, 179, 5);
        check(Math.Abs(MapGeometry.WrappedDelta(west.X, east.X, 5)) < 60, "map dateline wraps using nearby world copy");
        using (var places = JsonDocument.Parse("[{\"lat\":\"31.2\",\"lon\":\"121.5\",\"name\":\"上海\",\"display_name\":\"上海, 中国\"},{\"lat\":40.7,\"lon\":-74.0,\"display_name\":\"纽约, 美国\"},{\"lat\":\"91\",\"lon\":\"0\",\"name\":\"无效\"},{\"lat\":\"bad\",\"lon\":\"0\"}]"))
        {
            var placesParsed = MapSearchParser.Parse(places.RootElement);
            check(placesParsed.Count == 2, "map search parser rejects malformed and out-of-range coordinates");
            check(placesParsed[0].Name == "上海" && placesParsed[0].Address == "上海, 中国" && placesParsed[0].Latitude == 31.2 && placesParsed[0].Longitude == 121.5, "map search parser preserves localized name and coordinates");
            check(placesParsed[1].Name == "纽约" && placesParsed[1].Latitude == 40.7 && placesParsed[1].Longitude == -74.0, "map search parser accepts numeric coordinates and localized address fallback");
        }
        using (var notArray = JsonDocument.Parse("{}")) check(MapSearchParser.Parse(notArray.RootElement).Count == 0, "map search parser handles a non-array response");
        var photo = new PhotoItem { Filename = "TRAVEL.png", FilePath = Path.Combine(root, "trip", "TRAVEL.png"), DateTaken = new DateTime(2026, 10, 14), Category = "manual" };
        check(PhotoClassification.Suggest(photo, 0) == "2026-10", "date classification uses source timeline month");
        check(PhotoClassification.Suggest(photo, 1) == "trip" && PhotoClassification.Suggest(photo, 2) == "PNG", "folder and image-format classification");
        check(PhotoClassification.Suggest(photo, 3, aspect: .6) == "竖向照片" && PhotoClassification.Suggest(photo, 3, aspect: 1.6) == "横向照片" && PhotoClassification.Suggest(photo, 3, aspect: 1) == "方形照片", "orientation rules preserve original proportions");
        check(PhotoClassification.Suggest(photo, 3) == "", "unreadable orientation is not guessed");
        check(PhotoClassification.Suggest(photo, 4, "travel", "旅行") == "旅行" && PhotoClassification.Suggest(photo, 4, "family", "家人") == "", "filename keyword matching is case insensitive and scoped");
        check(photo.Category == "manual", "rule preview does not modify existing categories");
        Directory.CreateDirectory(Path.GetDirectoryName(photo.FilePath)!); await File.WriteAllTextAsync(photo.FilePath, "fixture");
        photo.MapLatitude = 31.2; photo.MapLongitude = 121.5; photo.MapPlaceId = "place-1"; photo.MapLabel = "上海"; photo.MapNote = "旅行批注 <script> & 原样保存";
        LibraryStore.ForgetMissingEntries(); await LibraryStore.SaveAsync(new[] { photo });
        var loaded = await LibraryStore.LoadAsync();
        var copy = loaded.Photos.Single();
        check(copy.MapLatitude == photo.MapLatitude && copy.MapLongitude == photo.MapLongitude && copy.MapPlaceId == "place-1" && copy.MapNote == photo.MapNote, "map position and plain text notes survive library roundtrip");
        check(await File.ReadAllTextAsync(photo.FilePath) == "fixture", "map metadata never modifies the source image");
        File.Delete(photo.FilePath); loaded = await LibraryStore.LoadAsync(); await LibraryStore.SaveAsync(loaded.Photos);
        check((await File.ReadAllTextAsync(LibraryStore.LibraryPath)).Contains("place-1"), "offline photo records keep map positions and notes");
    }
}
