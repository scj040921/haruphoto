namespace PhotoAlbum;

internal static class ViewerGeometry
{
    public static (double X, double Y, float Zoom) ZoomAt(double x, double y, float zoom,
        double pointerX, double pointerY, float multiplier, double width, double height)
    {
        var target = Math.Clamp(zoom * multiplier, 1, 8);
        var ratio = target / zoom;
        return (Math.Clamp((x + pointerX) * ratio - pointerX, 0, Math.Max(0, width * (target - 1))),
            Math.Clamp((y + pointerY) * ratio - pointerY, 0, Math.Max(0, height * (target - 1))), target);
    }
}
