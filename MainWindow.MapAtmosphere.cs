using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private void UpdateMapScale()
    {
        var metersPerPixel = 156543.033928 * Math.Cos(_mapLatitude * Math.PI / 180) / Math.Pow(2, _mapZoom);
        var span = metersPerPixel * 100;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(span)));
        var fraction = span / magnitude;
        var distance = (fraction >= 5 ? 5 : fraction >= 2 ? 2 : 1) * magnitude;
        MapScaleLine.Width = distance / metersPerPixel;
        MapScaleText.Text = distance >= 1000 ? $"{distance / 1000:0.#} 公里" : $"{distance:0} 米";
    }

    private void AnimateMapMarkerFocus(Button button, bool active)
    {
        if (!MotionEnabled) return;
        var visual = MapVisual(button);
        visual.CenterPoint = new Vector3((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(140);
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, active ? new Vector3(1.06f, 1.06f, 1) : Vector3.One);
        visual.StartAnimation("Scale", animation);
    }
}
