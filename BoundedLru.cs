namespace PhotoAlbum;

/// <summary>只保留最近使用的缩略图；可见项目由调用方保护，不会在浏览时被清空。</summary>
internal sealed class BoundedLru<T>(int capacity) where T : notnull
{
    private readonly LinkedList<T> _order = new();
    private readonly Dictionary<T, LinkedListNode<T>> _nodes = new();
    public int Count => _nodes.Count;

    public void Touch(T item)
    {
        if (_nodes.TryGetValue(item, out var existing)) _order.Remove(existing);
        _nodes[item] = _order.AddLast(item);
    }

    public void Trim(Func<T, bool> isProtected, Action<T> release)
    {
        var node = _order.First;
        while (_nodes.Count > capacity && node != null)
        {
            var next = node.Next;
            if (!isProtected(node.Value))
            {
                _order.Remove(node); _nodes.Remove(node.Value);
                release(node.Value);
            }
            node = next;
        }
    }
}
