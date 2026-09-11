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

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        _bounds.Clear();
        if (context.ItemCount == 0) return new Size(0, 0);

        var availableWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? MinColumnWidth
            : availableSize.Width;
        var columns = Math.Max(1, (int)Math.Floor((availableWidth + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)));
        var columnWidth = (availableWidth - ColumnSpacing * (columns - 1)) / columns;
        var heights = new double[columns];

        for (var index = 0; index < context.ItemCount; index++)
        {
            var column = 0;
            for (var candidate = 1; candidate < columns; candidate++)
                if (heights[candidate] < heights[column]) column = candidate;

            var element = context.GetOrCreateElementAt(index);
            var photo = context.GetItemAt(index) as PhotoItem;
            var bitmap = photo?.ThumbnailSource;
            var ratio = bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
                ? (double)bitmap.PixelWidth / bitmap.PixelHeight
                : photo?.ThumbnailAspectRatio ?? 4d / 3d;
            var height = columnWidth / ratio;
            if (photo != null) photo.ThumbnailHeight = height;
            // Force the image box to the measured ratio in this pass; the binding may update one pass later.
            if (element is Grid host && host.Children.Count > 0 && host.Children[0] is Border card
                && card.Child is Grid content && content.Children.Count > 0 && content.Children[0] is Border imageBox)
                imageBox.Height = height;
            element.Measure(new Size(columnWidth, height));
            var bounds = new Rect(column * (columnWidth + ColumnSpacing), heights[column], columnWidth, height);
            _bounds.Add(bounds);
            heights[column] += height + RowSpacing;
        }

        var contentHeight = 0d;
        foreach (var height in heights) contentHeight = Math.Max(contentHeight, height);
        if (contentHeight > 0) contentHeight -= RowSpacing;
        return new Size(availableWidth, contentHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var count = Math.Min(context.ItemCount, _bounds.Count);
        for (var index = 0; index < count; index++)
            context.GetOrCreateElementAt(index).Arrange(_bounds[index]);

        return finalSize;
    }
}
