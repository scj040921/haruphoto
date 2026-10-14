using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PhotoAlbum;

/// <summary>分页列表一次通知，避免逐张添加引起重复布局；相同页保持现有容器。</summary>
internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public bool ReplaceAll(IReadOnlyList<T> values, bool forceReset = false)
    {
        if (!forceReset && this.SequenceEqual(values)) return false;
        CheckReentrancy();
        Items.Clear();
        foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }
}
