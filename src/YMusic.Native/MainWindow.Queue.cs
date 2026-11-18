using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace YMusic.Native;

public partial class MainWindow
{
    public static readonly DependencyProperty QueuePlayingIndexProperty = DependencyProperty.Register(
        nameof(QueuePlayingIndex), typeof(int?), typeof(MainWindow), new PropertyMetadata(null));
    public int? QueuePlayingIndex => (int?)GetValue(QueuePlayingIndexProperty);

    private void Queue_Click(object sender, RoutedEventArgs e) => ShowQueue();
    private void ShowQueue() => ShowQueueCore(revealCurrent: true);
    private void ShowQueueCore(bool revealCurrent)
    {
        SetSection("queue", "Очередь воспроизведения");
        ShowQueueRows();
        SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex);
        SetStatus(CanDragQueue ? "Перетащите треки для изменения порядка. Ctrl/Shift — несколько треков, Ctrl+Z — вернуть удалённые."
            : "Перетаскивание доступно в исходном порядке без поиска. Кнопки выше меняют порядок воспроизведения.");
        if (revealCurrent)
        {
            var view = _viewGeneration; var items = _playbackQueue.Items;
            RequestCurrentTrackReveal(TracksList, () =>
            {
                SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex);
                return TracksList.SelectedIndex;
            }, () => _section == "queue" && view == _viewGeneration && ReferenceEquals(items, _playbackQueue.Items));
        }
    }
    private void ShowQueueRows()
    {
        QueueActions.Visibility = Visibility.Visible;
        ShowRows(OrderedQueueRows(CurrentListSort));
        UpdateIdleQueueMessage();
        UpdateQueueEditingControls();
    }

    private IReadOnlyList<TrackRow> OrderedQueueRows(ListSortRule rule)
    {
        if (rule.Field == ListSortField.Original) return _queue;
        // OrderBy preserves the order of equal keys for each occurrence of a row.
        var compare = new StableListComparer(rule);
        return _queue.OrderBy(row => row, Comparer<TrackRow>.Create(compare.Compare)).ToArray();
    }

    private void SortVisibleQueue(ListSortRule rule)
    {
        _currentTrackRevealRequest++;
        var selection = CaptureQueueSelection();
        var wasUpdating = _updatingTrackRows;
        _updatingTrackRows = true;
        try
        {
            _tracks.ReplaceAll(OrderedQueueRows(rule));
            RestoreQueueSelection(selection);
        }
        finally { _updatingTrackRows = wasUpdating; }
        UpdateControls();
    }
    private void History_Click(object sender, RoutedEventArgs e)
    {
        SetSection("queue-history", "История очереди"); ShowQueueHistoryRows();
        SetStatus("Предыдущие переходы в текущей очереди на этом компьютере.");
    }
    private void ShowQueueHistoryRows() => ShowRows(_playbackQueue.History.Reverse());
    private void Shuffle_Click(object sender, RoutedEventArgs e)
    { _playbackQueue.Shuffle = !_playbackQueue.Shuffle; UpdateControls(); }
    private void Repeat_Click(object sender, RoutedEventArgs e)
    { _playbackQueue.Repeat = (RepeatMode)(((int)_playbackQueue.Repeat + 1) % 3); UpdateControls(); }
    private void Enqueue_Click(object sender, RoutedEventArgs e) => AddSelectedToQueue(false);
    private void PlayNext_Click(object sender, RoutedEventArgs e) => AddSelectedToQueue(true);
    private void AddSelectedToQueue(bool next)
    {
        if (TracksList.SelectedItem is not TrackRow { CanPlay: true } row) return;
        _playbackQueue.Enqueue(row, next);
        if (_section == "queue") ShowQueueCore(revealCurrent: false);
        SetStatus(next ? "Трек будет играть следующим" : "Трек добавлен в конец очереди"); UpdateControls();
    }
    private int SelectedQueueIndex()
    {
        if (TracksList.SelectedItem is not TrackRow selected) return -1;
        var ordinal = _view.Cast<TrackRow>().Take(TracksList.SelectedIndex + 1).Count(row => ReferenceEquals(row, selected));
        for (var i = 0; i < _queue.Count; i++) if (ReferenceEquals(_queue[i], selected) && --ordinal == 0) return i;
        return -1;
    }
    private int FirstVisiblePlayableQueueIndex() => VisibleQueueOccurrences().Where(item => item.Row.CanPlay)
        .Select(item => item.QueueIndex).FirstOrDefault(-1);
    private long[] CaptureQueueSelection()
    {
        if (TracksList.SelectedItems.Count > 1)
            return SelectedQueueIndices().Select(_playbackQueue.OccurrenceTokenAt).ToArray();
        var index = SelectedQueueIndex();
        return index < 0 ? [] : [_playbackQueue.OccurrenceTokenAt(index)];
    }
    private async void RemoveQueue_Click(object sender, RoutedEventArgs e) => await RemoveSelectedQueueAsync();
    private async Task RemoveSelectedQueueAsync()
    {
        if (_section != "queue") return;
        var indices = SelectedQueueIndices(); if (indices.Length == 0) return;
        var removingCurrent = indices.Contains(_playbackQueue.CurrentIndex);
        var continuePlaying = removingCurrent && _player.IsPlaying;
        var next = WithQueueSelectionViewport(() =>
        {
            var replacement = _playbackQueue.RemoveMany(indices);
            if (removingCurrent) CueAfterQueueRemoval(replacement);
            ShowEditedQueue([]);
            if (removingCurrent) SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex);
            return replacement;
        });
        if (continuePlaying && next is not null && CanNavigateToTrack(next)) await PlayTrackAsync(next);
        UpdateControls(); UpdateProgress();
        SavePlaybackState();
    }

    private void CueAfterQueueRemoval(TrackRow? replacement)
    {
        _playbackGeneration++;
        _advanceOperation++;
        _advancingPlayback = false;
        CancelPendingWavePlayback();
        _playCts?.Cancel();
        _playCts = null;
        _loadingStream = false;
        CancelNextPreparation();
        ReportWaveEnd(false);
        _player.Stop();
        _progressTimer.Stop();
        _seekGesture?.Cancel();
        _expandedSeekGesture?.Cancel();
        PlaybackTimeline.Stop(SeekSlider);
        _pendingSeek = null;
        _seekDragging = false;
        ClearRestoredPosition();
        if (_current is not null) _current.IsPlaying = false;
        HighlightTrack(null);
        _current = replacement;
        PlaybackOptionsTrackChanged();
        SetPlayerArtwork(replacement);
        QualityText.Text = "";
        if (replacement is null) UpdateIdleQueueMessage();
        else
        {
            TrackTitle.Text = replacement.Title;
            TrackArtist.Text = replacement.Artist;
        }
        _diagnostics.Stage = replacement is null ? "library_ready" : "paused";
    }

    private Task PlayExistingQueueAsync()
    {
        if (_playbackQueue.Current is { } current && CanNavigateToTrack(current))
            return PlayTrackAsync(current);
        if (PeekPlayableNext(naturalEnd: false) is not null && _playbackQueue.Next() is { } next)
            return PlayTrackAsync(next);
        var items = _queue;
        for (var index = 0; index < items.Count; index++)
            if (CanNavigateToTrack(items[index]) && _playbackQueue.SelectAt(index)) return PlayTrackAsync(items[index]);
        return Task.CompletedTask;
    }
    private void UpdateIdleQueueMessage()
    {
        if (_current is not null || _playbackQueue.Current is not null) return;
        var empty = _queue.Count == 0;
        TrackTitle.Text = empty ? "Очередь пуста" : "Выберите трек";
        TrackArtist.Text = empty ? "Добавьте треки в очередь" : "Двойной щелчок или кнопка «Слушать»";
        QualityText.Text = "";
    }
    private void MoveQueueUp_Click(object sender, RoutedEventArgs e) => MoveSelectedQueue(-1);
    private void MoveQueueDown_Click(object sender, RoutedEventArgs e) => MoveSelectedQueue(1);
    private void MoveSelectedQueue(int direction)
    {
        if (_section != "queue") return;
        var indices = SelectedQueueIndices();
        if (indices.Length == 0 || direction < 0 && indices[0] == 0 || direction > 0 && indices[^1] == _queue.Count - 1) return;
        var gap = direction < 0 ? indices[0] - 1 : indices[^1] + 2;
        MoveQueueSelection(indices, gap);
    }
    private void UpdateQueueControls()
    {
        if (ShuffleButton is null) return;
        // Keep the compact transport controls icon-only; state belongs in colour,
        // the indicator and the accessible name, rather than clipped text.
        ShuffleButton.Foreground = (System.Windows.Media.Brush)FindResource(_playbackQueue.Shuffle ? "AccentTextBrush" : "MutedBrush");
        ShuffleIndicator.Visibility = _playbackQueue.Shuffle ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.ToolTip = _playbackQueue.Shuffle ? "Перемешивание включено" : "Включить перемешивание";
        System.Windows.Automation.AutomationProperties.SetName(ShuffleButton, (string)ShuffleButton.ToolTip);
        RepeatButton.Foreground = (System.Windows.Media.Brush)FindResource(_playbackQueue.Repeat == RepeatMode.Off ? "MutedBrush" : "AccentTextBrush");
        RepeatIndicator.Visibility = _playbackQueue.Repeat == RepeatMode.Off ? Visibility.Collapsed : Visibility.Visible;
        RepeatOneLabel.Visibility = _playbackQueue.Repeat == RepeatMode.One ? Visibility.Visible : Visibility.Collapsed;
        RepeatButton.ToolTip = _playbackQueue.Repeat switch { RepeatMode.All => "Повтор всей очереди", RepeatMode.One => "Повтор трека", _ => "Повтор выключен" };
        System.Windows.Automation.AutomationProperties.SetName(RepeatButton, (string)RepeatButton.ToolTip);
    }
    private void RefreshPlaybackView()
    {
        if (_section == "queue") { ShowQueueRows(); SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex); }
        else if (_section == "queue-history") ShowQueueHistoryRows();
    }
    private void SelectVisibleQueueOccurrence(int queueIndex)
    {
        var visible = VisibleQueueIndex(queueIndex);
        if (visible < 0) TracksList.UnselectAll();
        else if (TracksList.SelectedIndex != visible || TracksList.SelectedItems.Count != 1)
        {
            TracksList.SelectedIndex = -1;
            TracksList.SelectedIndex = visible;
        }
    }

    private void UpdateQueuePlayingIndex()
    {
        int? index = null;
        if (_section == "queue")
            index = _current is { IsPlaying: true } && _player.HasSource && ReferenceEquals(_current, _playbackQueue.Current)
                ? VisibleQueueIndex(_playbackQueue.CurrentIndex) : -1;
        SetValue(QueuePlayingIndexProperty, index);
    }

    private int VisibleQueueIndex(int queueIndex)
    {
        if (queueIndex >= 0 && queueIndex < _queue.Count)
        {
            var target = _queue[queueIndex];
            var ordinal = _queue.Take(queueIndex + 1).Count(row => ReferenceEquals(row, target));
            var visible = 0;
            foreach (TrackRow row in _view)
            {
                if (ReferenceEquals(row, target) && --ordinal == 0) return visible;
                visible++;
            }
        }
        return -1;
    }
}

public sealed class TrackRowPlaybackHighlight : IMultiValueConverter
{
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.RegisterAttached(
        "IsCurrent", typeof(bool), typeof(TrackRowPlaybackHighlight), new PropertyMetadata(false));
    public static bool GetIsCurrent(DependencyObject element) => (bool)element.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(DependencyObject element, bool value) => element.SetValue(IsCurrentProperty, value);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values[0] is int currentIndex
            ? currentIndex >= 0 && values[1] is int rowIndex && rowIndex == currentIndex
            : values[2] is true;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
