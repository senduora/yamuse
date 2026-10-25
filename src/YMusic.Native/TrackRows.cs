using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace YMusic.Native;

// WPF does not support range Add notifications. One Reset lets the view and its
// source-rank index rebuild once instead of processing thousands of insertions.
internal sealed class TrackRows : ObservableCollection<TrackRow>
{
    internal void ReplaceAll(IReadOnlyList<TrackRow> rows)
    {
        if (Count == rows.Count && this.Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
