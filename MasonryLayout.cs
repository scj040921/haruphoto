using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace PhotoAlbum;

/// <summary>
/// A compact column-based layout for the paged photo repeater.  It lays each
/// card into the currently shortest column, so portrait and landscape images
/// retain their visual rhythm instead of leaving grid-sized gaps.
/// </summary>
public sealed class MasonryLayout : VirtualizingLayout
{
    public double MinColumnWidth { get; set; } = 220;
    public double ColumnSpacing { get; set; } = 14;
    public double RowSpacing { get; set; } = 14;

    private readonly List<Rect> _bounds = new();
    private readonly List<int> _realized = new();
    private bool _geometryDirty = true;
    private double _geometryWidth = double.NaN, _geometryHeight;
    private int _geometryCount = -1;
    public void MarkGeometryDirty() => _geometryDirty = true;
#if PERFORMANCE_TEST
    public string Diagnostics { get; private set; } = "";
    public int GeometryBuilds { get; private set; }
#endif

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        _realized.Clear();
        if (context.ItemCount == 0) { _bounds.Clear(); _geometryDirty = true; return new Size(0, 0); }

        var availableWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? MinColumnWidth
            : availableSize.Width;
        if (_geometryDirty || _geometryCount != context.ItemCount || Math.Abs(_geometryWidth - availableWidth) > 0.01)
        {
            _bounds.Clear();
#if PERFORMANCE_TEST
            GeometryBuilds++;
#endif
            var columns = Math.Max(1, (int)Math.Floor((availableWidth + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)));
            var columnWidth = (availableWidth - ColumnSpacing * (columns - 1)) / columns;
            var heights = new double[columns];

            for (var index = 0; index < context.ItemCount; index++)
            {
                var column = 0;
                for (var candidate = 1; candidate < columns; candidate++)
                    if (heights[candidate] < heights[column]) column = candidate;

                var photo = context.GetItemAt(index) as PhotoItem;
                var bitmap = photo?.ThumbnailSource;
                var ratio = bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
                    ? (double)bitmap.PixelWidth / bitmap.PixelHeight
                    : photo?.ThumbnailAspectRatio ?? 4d / 3d;
                var height = columnWidth / ratio;
                var bounds = new Rect(column * (columnWidth + ColumnSpacing), heights[column], columnWidth, height);
                _bounds.Add(bounds);
                heights[column] += height + RowSpacing;
            }

            var calculatedHeight = 0d;
            foreach (var height in heights) calculatedHeight = Math.Max(calculatedHeight, height);
            if (calculatedHeight > 0) calculatedHeight -= RowSpacing;

            _geometryWidth = availableWidth;
            _geometryCount = context.ItemCount;
            _geometryHeight = calculatedHeight;
            _geometryDirty = false;
        }
        var contentHeight = _geometryHeight;

        // Only instantiate cards near the viewport. Geometry for every item is
        // cheap and keeps column positions stable as the repeater scrolls.
        var viewport = context.RealizationRect;
        var hasViewport = double.IsFinite(viewport.Y) && double.IsFinite(viewport.Height) && viewport.Height > 0;
        var top = hasViewport ? Math.Max(0, viewport.Y - 256) : 0;
        var bottom = hasViewport ? viewport.Y + viewport.Height + 256 : double.PositiveInfinity;
        for (var index = 0; index < _bounds.Count; index++)
        {
            var bounds = _bounds[index];
            if (bounds.Bottom < top || bounds.Top > bottom) continue;
            var element = context.GetOrCreateElementAt(index);
            var photo = context.GetItemAt(index) as PhotoItem;
            if (photo != null) photo.ThumbnailHeight = bounds.Height;
            if (element is Grid host && host.Children.Count > 0 && host.Children[0] is Border card
                && card.Child is Grid content && content.Children.Count > 0 && content.Children[0] is Border imageBox)
                imageBox.Height = bounds.Height;
            element.Measure(new Size(bounds.Width, bounds.Height));
            _realized.Add(index);
        }
#if PERFORMANCE_TEST
        Diagnostics = $"viewport={viewport}; realized={string.Join(',', _realized)}; totalHeight={contentHeight:F0}";
#endif
        return new Size(availableWidth, contentHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        foreach (var index in _realized)
            if (index < context.ItemCount && index < _bounds.Count)
                context.GetOrCreateElementAt(index).Arrange(_bounds[index]);

        return finalSize;
    }
}
