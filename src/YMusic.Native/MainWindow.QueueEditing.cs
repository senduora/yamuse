using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly record struct QueueInputStamp(long Version, int View, long Presentation, int Account, string? Uid);
    private sealed record QueuePress(Point Origin, int Index, long Token, QueueInputStamp Stamp, bool DelaySelection, int[]? Selection)
    {
        internal QueueInputStamp? ReleaseStamp { get; init; }
    }
    private sealed record QueueDrag(string Nonce, QueueInputStamp Stamp, int[] Indices);
    private QueueDrag? _activeQueueDrag;
    private QueuePress? _queuePress;
    private long _queuePresentationGeneration;
    private QueueDropLine? _queueDropLine;
    private AdornerLayer? _queueDropLayer;
    private bool CanDragQueue => _section == "queue" && _query.Length == 0 && CurrentListSort.Field == ListSortField.Original;

    private void InitializeQueueEditing()
    {
        TracksList.AllowDrop = true;
        _view.CollectionChanged += (_, _) => _queuePresentationGeneration++;
        TracksList.PreviewMouseLeftButtonDown += QueueDragDown;
        TracksList.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(QueueDragSelectionSettled), true);
        TracksList.PreviewMouseLeftButtonUp += QueueDragUp;
        TracksList.PreviewMouseMove += QueueDragMove;
        TracksList.DragOver += QueueDragOver;
        TracksList.Drop += QueueDrop;
        TracksList.DragLeave += (_, _) => ClearQueueDropLine();
    }

    private int[] SelectedQueueIndices() => ReadSelectedQueueIndices(_queue);

    private int[] ReadSelectedQueueIndices(IReadOnlyList<TrackRow> queue)
    {
        var visible = _view.Cast<TrackRow>().ToArray();
        var selectedCounts = new Dictionary<TrackRow, int>(ReferenceEqualityComparer.Instance);
        foreach (TrackRow row in TracksList.SelectedItems) selectedCounts[row] = selectedCounts.GetValueOrDefault(row) + 1;
        if (selectedCounts.Count == 0) return [];
        var counts = new Dictionary<TrackRow, int>(ReferenceEqualityComparer.Instance);
        foreach (var row in visible) counts[row] = counts.GetValueOrDefault(row) + 1;
        var selected = new bool[visible.Length]; var ambiguous = new List<int>();
        for (var i = 0; i < visible.Length; i++)
        {
            var count = selectedCounts.GetValueOrDefault(visible[i]);
            if (count == 0) continue;
            if (count == counts[visible[i]]) selected[i] = true;
            else ambiguous.Add(i);
        }
        if (ambiguous.Count > 0 && !WithQueueSelectionViewport(() =>
        {
            var panel = CurrentTrackItemsHost(TracksList);
            foreach (var index in ambiguous)
            {
                // SelectedItems contains references, not occurrence indices.
                // Recreate only ambiguous duplicate containers to read WPF's
                // exact selection after their earlier containers were recycled.
                if (QueueOccurrenceContainer(index, panel) is not { } container) return false;
                selected[index] = container.IsSelected;
            }
            return true;
        })) return []; // Never perform a partial destructive edit if layout is unavailable.
        var queueSelection = new bool[queue.Count];
        foreach (var occurrence in VisibleQueueOccurrences(queue))
            if (selected[occurrence.ViewIndex]) queueSelection[occurrence.QueueIndex] = true;
        return Enumerable.Range(0, queueSelection.Length).Where(index => queueSelection[index]).ToArray();
    }

    private IEnumerable<(int ViewIndex, int QueueIndex, TrackRow Row, bool Repeated)> VisibleQueueOccurrences(IReadOnlyList<TrackRow>? snapshot = null)
    {
        var queue = snapshot ?? _queue;
        var positions = new Dictionary<TrackRow, Queue<int>>(ReferenceEqualityComparer.Instance);
        var repeats = new HashSet<TrackRow>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < queue.Count; i++)
        {
            var row = queue[i];
            if (positions.TryGetValue(row, out var indices)) repeats.Add(row);
            else positions[row] = indices = new();
            indices.Enqueue(i);
        }
        var viewIndex = 0;
        foreach (TrackRow row in _view)
        {
            if (positions.TryGetValue(row, out var indices) && indices.TryDequeue(out var queueIndex))
                yield return (viewIndex, queueIndex, row, repeats.Contains(row));
            viewIndex++;
        }
    }

    private void RestoreQueueSelection(IReadOnlyCollection<long> tokens)
    {
        if (tokens.Count == 0) { TracksList.UnselectAll(); return; }
        var wanted = tokens.ToHashSet();
        if (tokens.Count == 1)
        {
            var index = Enumerable.Range(0, _queue.Count).FirstOrDefault(i => wanted.Contains(_playbackQueue.OccurrenceTokenAt(i)), -1);
            SelectVisibleQueueOccurrence(index);
            return;
        }
        var visible = VisibleQueueOccurrences().Select(item =>
            (item.ViewIndex, item.QueueIndex, item.Row, item.Repeated, Selected: wanted.Contains(_playbackQueue.OccurrenceTokenAt(item.QueueIndex)))).ToArray();
        if (visible.Length == TracksList.Items.Count && visible.All(item => item.Selected))
        {
            if (TracksList.SelectedItems.Count != visible.Length) TracksList.SelectAll();
            return;
        }
        var current = SelectedQueueIndices().ToHashSet();
        var repeatedChanges = new List<(int Index, bool Selected)>();
        foreach (var item in visible)
        {
            if (item.Selected == current.Contains(item.QueueIndex)) continue;
            if (item.Repeated) repeatedChanges.Add((item.ViewIndex, item.Selected));
            else if (item.Selected) TracksList.SelectedItems.Add(item.Row);
            else TracksList.SelectedItems.Remove(item.Row);
        }
        if (repeatedChanges.Count == 0) return;
        WithQueueSelectionViewport(() =>
        {
            var panel = CurrentTrackItemsHost(TracksList);
            foreach (var change in repeatedChanges)
                if (QueueOccurrenceContainer(change.Index, panel) is { } container) container.IsSelected = change.Selected;
            return true;
        });
    }

    internal void MoveQueueSelection(int[] indices, int gap)
    {
        var tokens = indices.Select(_playbackQueue.OccurrenceTokenAt).ToArray();
        _playbackQueue.MoveMany(indices, gap);
        WithQueueSelectionViewport(() =>
        {
            ShowEditedQueue(tokens);
            return true;
        });
        UpdateControls(); SavePlaybackState();
    }

    private void ShowEditedQueue(IReadOnlyCollection<long> selection)
    {
        _currentTrackRevealRequest++;
        _filterTimer.Stop();
        _query = FilterBox.Text.Trim();
        ShowQueueRows();
        RestoreQueueSelection(selection);
    }

    private T WithQueueSelectionViewport<T>(Func<T> action)
    {
        var viewer = FindViewer(TracksList); var horizontal = viewer?.HorizontalOffset ?? 0; var vertical = viewer?.VerticalOffset ?? 0;
        try { return action(); }
        finally
        {
            if (viewer is not null)
            {
                viewer.ScrollToHorizontalOffset(horizontal); viewer.ScrollToVerticalOffset(vertical);
                TracksList.UpdateLayout();
            }
        }
    }
    private ListViewItem? QueueOccurrenceContainer(int index, VirtualizingPanel? panel)
    {
        if (TracksList.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem container) return container;
        panel?.BringIndexIntoViewPublic(index);
        TracksList.UpdateLayout();
        return TracksList.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
    }
    private void UndoQueueRemoval_Click(object sender, RoutedEventArgs e)
    {
        if (_section != "queue" || !_playbackQueue.CanUndoRemoval) return;
        var selection = CaptureQueueSelection();
        if (!_playbackQueue.UndoRemoval()) return;
        WithQueueSelectionViewport(() =>
        {
            ShowEditedQueue(selection);
            if (selection.Length == 0 && _playbackQueue.Current is null)
                SelectVisibleQueueOccurrence(FirstVisiblePlayableQueueIndex());
            return true;
        });
        UpdateControls(); SavePlaybackState();
        SetStatus("Удалённые треки возвращены · воспроизведение сохранено");
    }
    private void UpdateQueueEditingControls()
    { if (QueueUndoButton is not null) QueueUndoButton.IsEnabled = _playbackQueue.CanUndoRemoval; }

    private void QueueDragDown(object sender, MouseButtonEventArgs e)
    {
        _queuePress = null;
        if (!CanDragQueue || e.ClickCount != 1 || FromButton(e.OriginalSource as DependencyObject)) return;
        if (ItemsControl.ContainerFromElement(TracksList, e.OriginalSource as DependencyObject) is not ListViewItem row) return;
        var index = TracksList.ItemContainerGenerator.IndexFromContainer(row);
        if (index < 0 || index >= _queue.Count || !ReferenceEquals(row.Content, _queue[index])) return;
        var delayed = row.IsSelected && TracksList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None;
        var stamp = CaptureQueueInputStamp();
        _queuePress = new(e.GetPosition(TracksList), index, _playbackQueue.OccurrenceTokenAt(index), stamp, delayed,
            delayed ? SelectedQueueIndices() : null);
        if (delayed) e.Handled = true;
    }

    private void QueueDragSelectionSettled(object sender, MouseButtonEventArgs e)
    {
        // ListViewItem has now processed the same physical mouse down, so the
        // snapshot includes WPF's exact single/range/toggle selection.
        if (e.ChangedButton != MouseButton.Left || _queuePress is not { DelaySelection: false, Selection: null } press || !OwnsQueuePress(press)) return;
        int[] selection = TracksList.SelectedItems.Count == 1 && TracksList.SelectedIndex >= 0
            ? [TracksList.SelectedIndex] : SelectedQueueIndices();
        _queuePress = press with { Selection = selection };
    }

    private void QueueDragUp(object sender, MouseButtonEventArgs e)
    {
        var press = _queuePress;
        _queuePress = null;
        if (press is not { DelaySelection: true } || !OwnsQueueInput(press.ReleaseStamp ?? press.Stamp)) return;
        var index = TrackRowIndex(e.OriginalSource as DependencyObject);
        if (index < 0 || index >= _queue.Count || _playbackQueue.OccurrenceTokenAt(index) != press.Token
            || TracksList.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem row
            || !ReferenceEquals(row.Content, _queue[index])) return;

        // ListBox updates its range anchor on selection changes in Single mode.
        // Restore Extended before the next Shift gesture.
        var mode = TracksList.SelectionMode;
        TracksList.SetCurrentValue(ListBox.SelectionModeProperty, SelectionMode.Single);
        try
        {
            TracksList.SelectedIndex = -1;
            TracksList.SelectedIndex = index;
        }
        finally { TracksList.SetCurrentValue(ListBox.SelectionModeProperty, mode); }
        if (row.IsSelected && ReferenceEquals(TracksList.ItemContainerGenerator.ContainerFromIndex(index), row)) row.Focus();
    }

    private void QueueDragMove(object sender, MouseEventArgs e)
    {
        if (_queuePress is not { } press) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _queuePress = null; return; }
        var point = e.GetPosition(TracksList);
        if (Math.Abs(point.X - press.Origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - press.Origin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            if (!OwnsQueueInput(press.ReleaseStamp ?? press.Stamp)) _queuePress = null;
            return;
        }
        if (TakeQueueDrag() is not { } drag) return;
        _activeQueueDrag = drag;
        // Only a string capability crosses OLE; never deserialize an object
        // supplied by another process. Queue indices remain private to this window.
        try { DragDrop.DoDragDrop(TracksList, new DataObject(DataFormats.UnicodeText, drag.Nonce), DragDropEffects.Move); }
        finally { _activeQueueDrag = null; ClearQueueDropLine(); }
        e.Handled = true;
    }

    private QueueInputStamp CaptureQueueInputStamp() => new(_playbackQueue.Version, _viewGeneration, _queuePresentationGeneration, _accountGeneration, _account?.Uid);

    private void RetainDelayedQueueClick(QueueInputStamp beforeAppend)
    {
        if (_queuePress is not { DelaySelection: true } press || (press.ReleaseStamp ?? press.Stamp) != beforeAppend) return;
        var now = CaptureQueueInputStamp();
        // Only the click may survive a passive refill. A drag still owns its original pointer-down stamp.
        _queuePress = beforeAppend with { Version = now.Version, Presentation = now.Presentation } == now && OwnsQueueInput(now)
            && Enumerable.Range(0, _queue.Count).Any(index => _playbackQueue.OccurrenceTokenAt(index) == press.Token)
            ? press with { ReleaseStamp = now } : null;
    }

    private bool OwnsQueueInput(QueueInputStamp stamp) => CanDragQueue && HasTrackListPlaybackView && !IsExpandedPlayerOpen
        && stamp == CaptureQueueInputStamp();

    private bool OwnsQueuePress(QueuePress press) => OwnsQueueInput(press.Stamp)
        && press.Index < _queue.Count && _playbackQueue.OccurrenceTokenAt(press.Index) == press.Token;

    private QueueDrag? TakeQueueDrag()
    {
        var press = _queuePress;
        _queuePress = null;
        return press is { Selection: { Length: > 0 } selection } && OwnsQueuePress(press)
            ? new(Guid.NewGuid().ToString("N"), press.Stamp, selection) : null;
    }

    private QueueDrag? ValidQueueDrag(DragEventArgs e)
    {
        if (!CanDragQueue || _activeQueueDrag is not { } d || !e.Data.GetDataPresent(DataFormats.UnicodeText, false)
            || e.Data.GetData(DataFormats.UnicodeText, false) is not string nonce || nonce != d.Nonce) return null;
        return OwnsQueueInput(d.Stamp) ? d : null;
    }

    private int QueueDropGap(DragEventArgs e, bool showLine)
    {
        if (ItemsControl.ContainerFromElement(TracksList, e.OriginalSource as DependencyObject) is not ListViewItem row)
        { if (showLine) ClearQueueDropLine(); return _queue.Count; }
        var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        if (showLine)
        {
            ClearQueueDropLine(); _queueDropLayer = AdornerLayer.GetAdornerLayer(row);
            _queueDropLine = new QueueDropLine(row, after, (Brush)FindResource("AccentTextBrush")) { IsHitTestVisible = false }; _queueDropLayer?.Add(_queueDropLine);
        }
        return TracksList.ItemContainerGenerator.IndexFromContainer(row) + (after ? 1 : 0);
    }
    private void QueueDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ValidQueueDrag(e) is null ? DragDropEffects.None : DragDropEffects.Move;
        if (e.Effects == DragDropEffects.Move) QueueDropGap(e, true); else ClearQueueDropLine();
        e.Handled = true;
    }
    private void QueueDrop(object sender, DragEventArgs e)
    {
        var drag = ValidQueueDrag(e); var gap = QueueDropGap(e, false); ClearQueueDropLine();
        if (drag is not null) MoveQueueSelection(drag.Indices, gap);
        e.Effects = drag is null ? DragDropEffects.None : DragDropEffects.Move; e.Handled = true;
    }
    private void ClearQueueDropLine()
    { if (_queueDropLine is not null) _queueDropLayer?.Remove(_queueDropLine); _queueDropLine = null; _queueDropLayer = null; }
    private sealed class QueueDropLine(UIElement element, bool after, Brush brush) : Adorner(element)
    {
        protected override void OnRender(DrawingContext context)
        { var y = after ? AdornedElement.RenderSize.Height - 1 : 1; context.DrawLine(new Pen(brush, 3), new(0, y), new(AdornedElement.RenderSize.Width, y)); }
    }
}
