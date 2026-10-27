using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private sealed record QueueDrag(string Nonce, long Version, int Account, string? Uid, int[] Indices);
    private QueueDrag? _activeQueueDrag;
    private Point? _queueDragOrigin;
    private int _queueDragRow = -1;
    private bool _queueDelaySelection;
    private QueueDropLine? _queueDropLine;
    private AdornerLayer? _queueDropLayer;
    private bool CanDragQueue => _section == "queue" && _query.Length == 0 && CurrentListSort.Field == ListSortField.Original;

    private void InitializeQueueEditing()
    {
        TracksList.AllowDrop = true;
        TracksList.PreviewMouseLeftButtonDown += QueueDragDown;
        TracksList.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_queueDelaySelection && _queueDragRow >= 0) TracksList.SelectedIndex = _queueDragRow;
            _queueDelaySelection = false; _queueDragOrigin = null;
        };
        TracksList.PreviewMouseMove += QueueDragMove;
        TracksList.DragOver += QueueDragOver;
        TracksList.Drop += QueueDrop;
        TracksList.DragLeave += (_, _) => ClearQueueDropLine();
    }

    private int[] SelectedQueueIndices()
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
        var occurrences = new Dictionary<TrackRow, Queue<int>>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _queue.Count; i++)
        {
            if (!occurrences.TryGetValue(_queue[i], out var positions)) occurrences[_queue[i]] = positions = new();
            positions.Enqueue(i);
        }
        var queueSelection = new bool[_queue.Count];
        for (var i = 0; i < visible.Length; i++)
            if (occurrences.TryGetValue(visible[i], out var positions) && positions.TryDequeue(out var index) && selected[i]) queueSelection[index] = true;
        return Enumerable.Range(0, queueSelection.Length).Where(index => queueSelection[index]).ToArray();
    }

    internal void MoveQueueSelection(int[] indices, int gap)
    {
        var tokens = indices.Select(_playbackQueue.OccurrenceTokenAt).ToHashSet();
        _playbackQueue.MoveMany(indices, gap);
        WithQueueSelectionViewport(() =>
        {
            ShowQueueCore(revealCurrent: false);
            TracksList.SelectedItems.Clear();
            if (tokens.Count == 1)
            {
                var selected = Enumerable.Range(0, _queue.Count).Single(i => tokens.Contains(_playbackQueue.OccurrenceTokenAt(i)));
                SelectVisibleQueueOccurrence(selected);
                return true;
            }
            var occurrences = new Dictionary<TrackRow, Queue<bool>>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < _queue.Count; i++)
            {
                if (!occurrences.TryGetValue(_queue[i], out var selected)) occurrences[_queue[i]] = selected = new();
                selected.Enqueue(tokens.Contains(_playbackQueue.OccurrenceTokenAt(i)));
            }
            var duplicates = occurrences.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key).ToHashSet(ReferenceEqualityComparer.Instance);
            var panel = CurrentTrackItemsHost(TracksList); var index = 0;
            foreach (TrackRow row in _view)
            {
                if (occurrences[row].Dequeue())
                {
                    if (!duplicates.Contains(row)) TracksList.SelectedItems.Add(row);
                    else if (QueueOccurrenceContainer(index, panel) is { } container) container.IsSelected = true;
                }
                index++;
            }
            return true;
        });
        UpdateControls(); SavePlaybackState();
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
        if (_section != "queue" || !_playbackQueue.UndoRemoval()) return;
        ShowQueueCore(revealCurrent: false);
        if (_playbackQueue.Current is null)
        {
            var firstPlayable = Enumerable.Range(0, _queue.Count).FirstOrDefault(i => _queue[i].CanPlay, -1);
            if (firstPlayable >= 0) SelectVisibleQueueOccurrence(firstPlayable);
        }
        UpdateControls(); SavePlaybackState();
        SetStatus("Удалённые треки возвращены · воспроизведение сохранено");
    }
    private void UpdateQueueEditingControls()
    { if (QueueUndoButton is not null) QueueUndoButton.IsEnabled = _playbackQueue.CanUndoRemoval; }

    private void QueueDragDown(object sender, MouseButtonEventArgs e)
    {
        _queueDragOrigin = null; _queueDelaySelection = false;
        if (!CanDragQueue || e.ClickCount != 1 || FromButton(e.OriginalSource as DependencyObject)) return;
        if (ItemsControl.ContainerFromElement(TracksList, e.OriginalSource as DependencyObject) is not ListViewItem row) return;
        _queueDragRow = TracksList.ItemContainerGenerator.IndexFromContainer(row); _queueDragOrigin = e.GetPosition(TracksList);
        if (row.IsSelected && TracksList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        { _queueDelaySelection = true; e.Handled = true; }
    }

    private void QueueDragMove(object sender, MouseEventArgs e)
    {
        if (_queueDragOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed || !CanDragQueue) return;
        var point = e.GetPosition(TracksList);
        if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _queueDragOrigin = null; _queueDelaySelection = false;
        var indices = SelectedQueueIndices(); if (indices.Length == 0) return;
        var drag = _activeQueueDrag = new QueueDrag(Guid.NewGuid().ToString("N"), _playbackQueue.Version, _accountGeneration, _account?.Uid, indices);
        // Only a string capability crosses OLE; never deserialize an object
        // supplied by another process. Queue indices remain private to this window.
        try { DragDrop.DoDragDrop(TracksList, new DataObject(DataFormats.UnicodeText, drag.Nonce), DragDropEffects.Move); }
        finally { _activeQueueDrag = null; ClearQueueDropLine(); }
        e.Handled = true;
    }

    private QueueDrag? ValidQueueDrag(DragEventArgs e)
    {
        if (!CanDragQueue || _activeQueueDrag is not { } d || !e.Data.GetDataPresent(DataFormats.UnicodeText, false)
            || e.Data.GetData(DataFormats.UnicodeText, false) is not string nonce || nonce != d.Nonce) return null;
        return d.Version == _playbackQueue.Version
            && d.Account == _accountGeneration && d.Uid == _account?.Uid ? d : null;
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
