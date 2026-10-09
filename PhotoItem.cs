using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoAlbum;

/// <summary>
/// 照片数据模型。实现 INotifyPropertyChanged，收藏/评分/缩略图变化时 UI 自动更新，无需全量刷新。
/// </summary>
public sealed class PhotoItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Filename { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.Now;
    // 统一记录源文件的最后修改时间，不再使用 EXIF 拍摄日期。
    public DateTime? DateTaken { get; set; }

    /// <summary>时间线使用源文件修改时间；旧记录缺失时才退化到导入时间。</summary>
    public DateTime TimelineDate => DateTaken ?? DateAdded;

    private string _category = "";
    public string Category
    {
        get => _category;
        set
        {
            if (_category == value) return;
            _category = value ?? "";
            OnChanged();
            OnChanged(nameof(CategoryTagOpacity));
            OnChanged(nameof(CategoryDisplay));
        }
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnChanged();
            OnChanged(nameof(FavoriteIcon));
        }
    }

    private int _rating;
    public int Rating
    {
        get => _rating;
        set
        {
            var v = Math.Clamp(value, 0, 5);
            if (_rating == v) return;
            _rating = v;
            OnChanged();
            OnChanged(nameof(RatingStars));
        }
    }

    private BitmapImage? _thumbnailSource;
    private string _thumbnailStatus = "加载中…";
    public string ThumbnailStatus
    {
        get => _thumbnailStatus;
        set { if (_thumbnailStatus == value) return; _thumbnailStatus = value; OnChanged(); }
    }
    public BitmapImage? ThumbnailSource
    {
        get => _thumbnailSource;
        set
        {
            _thumbnailSource = value;
            ThumbnailStatus = value == null ? "加载中…" : "";
            OnChanged();
        }
    }

    private double _thumbnailHeight = 165;
    private double _thumbnailAspectRatio = 4d / 3d;
    /// <summary>缩略图宽高比；瀑布流据此在不同列宽下保持原图比例。</summary>
    public double ThumbnailAspectRatio
    {
        get => _thumbnailAspectRatio;
        set => _thumbnailAspectRatio = double.IsFinite(value) && value > 0 ? value : 4d / 3d;
    }

    /// <summary>以 220px 卡片宽度计算的缩略图高度，供瀑布流布局使用。</summary>
    public double ThumbnailHeight
    {
        get => _thumbnailHeight;
        set
        {
            var height = double.IsFinite(value) && value > 0 ? value : 165;
            if (Math.Abs(_thumbnailHeight - height) < 0.001) return;
            _thumbnailHeight = height;
            OnChanged();
        }
    }

    public string DisplayName => Filename.Length > 20 ? Filename[..17] + "…" : Filename;
    public string FavoriteIcon => IsFavorite ? "★" : "☆";
    public string RatingStars => new string('★', Rating) + new string('☆', 5 - Rating);
    public string SizeText => FileSize > 1048576 ? $"{FileSize / 1048576.0:F1} MB" : $"{FileSize / 1024.0:F0} KB";
    public string DateText => TimelineDate.ToString("yyyy-MM-dd HH:mm");
    public string CategoryDisplay => string.IsNullOrEmpty(Category) ? "" : Category;

    private bool _selectVisible;
    public bool SelectVisible
    {
        get => _selectVisible;
        set { if (_selectVisible == value) return; _selectVisible = value; OnChanged(nameof(SelectVisibleOpacity)); OnChanged(nameof(CategoryTagOpacity)); }
    }
    public double SelectVisibleOpacity => _selectVisible ? 1.0 : 0.0;
    public double CategoryTagOpacity => string.IsNullOrEmpty(Category) ? 0.0 : 1.0;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(nameof(SelectIcon)); }
    }
    public string SelectIcon => _isSelected ? "☑" : "☐";

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
