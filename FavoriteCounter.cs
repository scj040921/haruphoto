namespace PhotoAlbum;

/// <summary>维护图库收藏数，避免每次更新状态栏时重新扫描整个图库。</summary>
internal sealed class FavoriteCounter
{
    public int Value { get; private set; }

    public void Initialize(IEnumerable<PhotoItem> photos)
        => Value = photos.Count(photo => photo.IsFavorite);

    public void Set(PhotoItem photo, bool favorite)
    {
        if (photo.IsFavorite == favorite) return;
        Value += favorite ? 1 : -1;
        photo.IsFavorite = favorite;
    }

    public void Added(PhotoItem photo)
    {
        if (photo.IsFavorite) Value++;
    }

    public void Removed(PhotoItem photo)
    {
        if (photo.IsFavorite) Value = Math.Max(0, Value - 1);
    }

    public void Clear() => Value = 0;
}
