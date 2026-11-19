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
    private (IReadOnlyList<TrackRow> Rows, long[] Tokens, long Replacement, long? Cursor)? _shownQueue;

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
            var view = _viewGeneration; var queue = _playbackQueue.ReplacementVersion;
            RequestCurrentTrackReveal(TracksList, () =>
            {
                SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex);
                return TracksList.SelectedIndex;
            }, () => _section == "queue" && view == _viewGeneration && queue == _playbackQueue.ReplacementVersion);
        }
    }
    private void ShowQueueRows()
    {
        QueueActions.Visibility = Visibility.Visible;
        _shownQueue = (_queue, Enumerable.Range(0, _queue.Count).Select(_playbackQueue.OccurrenceTokenAt).ToArray(),
            _playbackQueue.ReplacementVersion, _playbackQueue.CurrentOccurrenceToken);
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
    {
        _playbackQueue.Shuffle = !_playbackQueue.Shuffle;
        if (_playbackStateRestoreUnavailable) _pendingRestoredShuffle = _playbackQueue.Shuffle;
        UpdateControls();
    }
    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _playbackQueue.Repeat = (RepeatMode)(((int)_playbackQueue.Repeat + 1) % 3);
        if (_playbackStateRestoreUnavailable) _pendingRestoredRepeat = _playbackQueue.Repeat;
        UpdateControls();
    }
    private void Enqueue_Click(object sender, RoutedEventArgs e) => AddSelectedToQueue(false);
    private void PlayNext_Click(object sender, RoutedEventArgs e) => AddSelectedToQueue(true);
    private void AddSelectedToQueue(bool next)
    {
        if (TracksList.SelectedItem is not TrackRow { CanPlay: true } row) return;
        _playbackQueue.Enqueue(CopyTrackForQueue(row), next);
        if (_section == "queue") ShowQueueCore(revealCurrent: false);
        SetStatus(next ? "Трек будет играть следующим" : "Трек добавлен в конец очереди"); UpdateControls();
    }
    private int SelectedQueueIndex() => ReadSelectedQueueIndex(_queue);

    private int ReadSelectedQueueIndex(IReadOnlyList<TrackRow> queue)
    {
        if (TracksList.SelectedItem is not TrackRow selected) return -1;
        var ordinal = _view.Cast<TrackRow>().Take(TracksList.SelectedIndex + 1).Count(row => ReferenceEquals(row, selected));
        for (var i = 0; i < queue.Count; i++) if (ReferenceEquals(queue[i], selected) && --ordinal == 0) return i;
        return -1;
    }
    private Task PlayFirstVisibleQueueOccurrenceAsync()
    {
        foreach (var occurrence in VisibleQueueOccurrences())
            if (CanNavigateToTrack(occurrence.Row)) return PlayTrackFromListAsync(occurrence.Row, -1, occurrence.QueueIndex);
        return Task.CompletedTask;
    }

    private int FirstVisiblePlayableQueueIndex() => VisibleQueueOccurrences().Where(item => item.Row.CanPlay)
        .Select(item => item.QueueIndex).FirstOrDefault(-1);

    private long[] CaptureQueueSelection() => ReadQueueSelection(_queue, _playbackQueue.OccurrenceTokenAt);

    private long[] ReadQueueSelection(IReadOnlyList<TrackRow> queue, Func<int, long> tokenAt)
    {
        if (TracksList.SelectedItems.Count > 1)
            return ReadSelectedQueueIndices(queue).Select(tokenAt).ToArray();
        var index = ReadSelectedQueueIndex(queue);
        return index < 0 ? [] : [tokenAt(index)];
    }
    private async void RemoveQueue_Click(object sender, RoutedEventArgs e) => await RemoveSelectedQueueAsync();
    private async Task RemoveSelectedQueueAsync()
    {
        if (_section != "queue") return;
        var indices = SelectedQueueIndices();
        if (indices.Length == 0) return;
        if (!indices.Contains(_playbackQueue.CurrentIndex))
        {
            RemoveRows();
            return;
        }

        var continueAfterEnd = NaturalPlaybackContinuationPending || OwnsDeferredQueueTrackEnd();
        var continueBeforeFirstPlay = QueuePlaybackBeforeFirstPlay;
        var continuePlaying = _player.IsPlaying || continueAfterEnd || continueBeforeFirstPlay;
        var version = _playbackQueue.Version;
        var queue = _playbackQueue.ReplacementVersion;
        var selectedOccurrences = indices.Select(_playbackQueue.OccurrenceTokenAt).ToHashSet();
        var current = _playbackQueue.CurrentOccurrenceToken;
        var playback = _playbackGeneration;
        var operation = ++_advanceOperation;
        CancelPendingWavePlayback();
        if (continueAfterEnd) RememberDeferredQueueTrackEnd();
        _advancingPlayback = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _queueAdmissionCts = cts;
        NativePlayer.LocalPlaybackAdmission? admission = null;
        var selectionLost = false;
        try
        {
            if (continueBeforeFirstPlay)
            {
                // Canceling the old open can refresh transport before this request owns it.
                _queueRemovalContinuation = CaptureQueuePlayback(cts);
                UpdateControls();
            }
            long? replacement = null;
            var streamOnly = false;
            var rejected = new HashSet<long>();
            while (MayRemove() && _playbackQueue.PeekRemovalReplacement(indices, rejected) is { } candidate)
            {
                streamOnly = false;
                var local = RefreshLocalPlaybackSource(candidate.Track);
                if (IsPlaybackSelectionBlockedByRemoval(candidate.Track)
                    || local is null && !CanStreamAfterLocalIntegrityFailure(candidate.Track))
                {
                    rejected.Add(candidate.Token);
                    continue;
                }
                if (local is { Bytes: not null })
                {
                    _pendingOfflinePlayback = (candidate.Track.Id, cts);
                    _manualQueueAdvanceCts = cts;
                    try
                    {
                        admission = await VerifyQueuePlaybackSourceAsync(candidate.Track, local, cts.Token);
                    }
                    catch (LocalAudioSourceException error)
                    {
                        if (!MayRemove()) return;
                        if (error is not LocalAudioIntegrityException || !CanStreamAfterLocalIntegrityFailure(candidate.Track))
                        {
                            rejected.Add(candidate.Token);
                            continue;
                        }
                        streamOnly = true;
                    }
                    if (!MayRemove()) return;
                    if (BlocksOfflineOpen(candidate.Track.Id))
                    {
                        admission?.Dispose();
                        admission = null;
                        rejected.Add(candidate.Token);
                        continue;
                    }
                    if (_playbackQueue.PeekRemovalReplacement(indices, rejected)?.Token != candidate.Token)
                    {
                        admission?.Dispose();
                        admission = null;
                        continue;
                    }
                }
                replacement = candidate.Token;
                break;
            }
            if (!MayRemove()) return;
            _queueAdmissionCts = null;
            continueAfterEnd |= OwnsDeferredQueueTrackEnd();
            var pause = _wavePauseRevision;
            var next = RemoveRows(removingCurrent: true, replacement);
            // Rendering the newly cued occurrence can synchronously expire sleep.
            if (continuePlaying && next is not null && !_closed && !cts.IsCancellationRequested
                && playback + 1 == _playbackGeneration && queue == _playbackQueue.ReplacementVersion
                && replacement == _playbackQueue.CurrentOccurrenceToken && ReferenceEquals(next, _current))
            {
                if (pause != _wavePauseRevision)
                {
                    RememberPausedDesktopOccurrence();
                    UpdateWindowsMedia();
                }
                else
                    await PlayTrackWithSourceAsync(next, null, false, localAdmission: admission, streamOnly: streamOnly,
                        continueAfterEnd: continueAfterEnd || continueBeforeFirstPlay);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            admission?.Dispose();
            var resume = selectionLost && OwnsQueuePlayback(_queueRemovalContinuation, _queueAdmissionCts)
                && ReferenceEquals(_queueRemovalContinuation?.Request, cts) && !_player.IsPlaying
                ? _playbackQueue.Current : null;
            if (ReferenceEquals(_queueAdmissionCts, cts)) _queueAdmissionCts = null;
            if (ReferenceEquals(_manualQueueAdvanceCts, cts)) _manualQueueAdvanceCts = null;
            if (ReferenceEquals(_queueRemovalContinuation?.Request, cts)) _queueRemovalContinuation = null;
            if (_pendingOfflinePlayback is { } pending && ReferenceEquals(pending.Request, cts)) _pendingOfflinePlayback = null;
            if (operation == _advanceOperation)
            {
                _advancingPlayback = false;
                if (resume is not null)
                    await PlayTrackWithSourceAsync(resume, null, false, operation, continueAfterEnd: true);
                else if (continueBeforeFirstPlay) UpdateControls();
                else RefreshNextPreparation();
                await ResumeDeferredQueueTrackEndAsync();
            }
        }

        bool MayRemove()
        {
            if (_closed || cts.IsCancellationRequested || operation != _advanceOperation || playback != _playbackGeneration
                || queue != _playbackQueue.ReplacementVersion || current != _playbackQueue.CurrentOccurrenceToken) return false;
            if (version != _playbackQueue.Version)
            {
                indices = _playbackQueue.IndicesOfOccurrences(selectedOccurrences);
                version = _playbackQueue.Version;
            }
            selectionLost = indices.Length != selectedOccurrences.Count;
            return !selectionLost;
        }

        TrackRow? RemoveRows(bool removingCurrent = false, long? replacement = null)
        {
            var next = WithQueueSelectionViewport(() =>
            {
                var selected = removingCurrent ? _playbackQueue.RemoveManyWithReplacement(indices, replacement) : _playbackQueue.RemoveMany(indices);
                if (removingCurrent) CueAfterQueueRemoval(selected);
                ShowEditedQueue([]);
                if (removingCurrent) SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex);
                return selected;
            });
            UpdateControls(); UpdateProgress();
            SavePlaybackState();
            return next;
        }
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
        LoadLyrics(refresh: false);
    }

    private Task PlayExistingQueueAsync()
    {
        if (_playbackQueue.Current is { } current && CanNavigateToTrack(current))
            return CanSelectWhileRemovingAudio(current) ? PlayTrackAsync(current) : Task.CompletedTask;
        if (PeekPlayableNext(naturalEnd: false) is { } candidate)
            return CanSelectWhileRemovingAudio(candidate) && _playbackQueue.Next() is { } next ? PlayTrackAsync(next) : Task.CompletedTask;
        var items = _queue;
        for (var index = 0; index < items.Count; index++)
            if (CanNavigateToTrack(items[index]))
                return CanSelectWhileRemovingAudio(items[index]) && _playbackQueue.SelectAt(index) ? PlayTrackAsync(items[index]) : Task.CompletedTask;
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
    private void RefreshPlaybackView() => RefreshPlaybackViewCore(null);

    private void RefreshPlaybackViewCore(QueueInputStamp? beforeAppend)
    {
        if (_section == "queue")
        {
            // Read the displayed occurrence tokens before rendering. Append can already have evicted earlier rows.
            var selection = _shownQueue is { } shown && shown.Replacement == _playbackQueue.ReplacementVersion
                && shown.Cursor == _playbackQueue.CurrentOccurrenceToken ? ReadQueueSelection(shown.Rows, index => shown.Tokens[index]) : null;
            ShowQueueRows();
            if (selection is null) SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex);
            else
            {
                RestoreQueueSelection(selection);
                if (beforeAppend is { } stamp) RetainDelayedQueueClick(stamp);
            }
        }
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
        else if (_section == "offline")
            index = _current is { } current && _player.HasSource && FindPlayingOfflineRow(current) is { } local
                ? PlaybackContextVisibleIndex((local, 1)) : -1;
        else if (IsPlaybackContextView())
            index = _current is not null && !_loadingStream && _player.HasSource && ReferenceEquals(_current, _playbackQueue.Current)
                && PlaybackContextCurrentOccurrence() is { } target ? PlaybackContextVisibleIndex(target) : -1;
        SetValue(QueuePlayingIndexProperty, index);
    }

    private TrackRow? FindPlayingOfflineRow(TrackRow current)
    {
        var path = _player.SourceLocalPath ?? (current.IsOfflineLibraryRow ? current.LocalPath : null);
        return path is null ? null : _tracks.FirstOrDefault(row => string.Equals(row.LocalPath, path, StringComparison.OrdinalIgnoreCase));
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
