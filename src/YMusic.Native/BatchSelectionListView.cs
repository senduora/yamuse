using System.Collections;
using System.Windows.Controls;

namespace YMusic.Native;

// Keep WPF's native extended selection, but publish a bulk replacement once.
public sealed class BatchSelectionListView : ListView
{
    internal void ReplaceSelection(IEnumerable items) => SetSelectedItems(items);
}
