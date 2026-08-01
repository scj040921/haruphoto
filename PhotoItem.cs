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
    public DateTime? DateTaken { get; set; }   // EXIF 拍摄时间，无则 null

    /// <summary>时间线使用的日期：优先拍摄日期，退化到添加日期。</summary>
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
    public BitmapImage? ThumbnailSource
    {
        get => _thumbnailSource;
        set
        {
            _thumbnailSource = value;
            OnChanged();
        }
    }

    public string DisplayName => Filename.Length > 20 ? Filename[..17] + "…" : Filename;
    public string FavoriteIcon => IsFavorite ? "★" : "☆";
    public string RatingStars => new string('★', Rating) + new string('☆', 5 - Rating);
    public string SizeText => FileSize > 1048576 ? $"{FileSize / 1048576.0:F1} MB" : $"{FileSize / 1024.0:F0} KB";
    public string DateText => DateAdded.ToString("yyyy-MM-dd HH:mm");
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
