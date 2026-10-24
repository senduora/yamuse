using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

public partial class MainWindow
{
    private void Queue_Click(object sender, RoutedEventArgs e) => ShowQueue();
    private void ShowQueue()
    {
        SetSection("queue", "Очередь воспроизведения"); QueueActions.Visibility = Visibility.Visible;
        ShowRows(_playbackQueue.Items);
        SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex);
        SetStatus("Порядок очереди можно менять кнопками выше. Двойной щелчок запускает выбранный трек.");
    }
    private void History_Click(object sender, RoutedEventArgs e)
    {
        SetSection("queue-history", "История очереди"); ShowRows(_playbackQueue.History.Reverse());
        SetStatus("Предыдущие переходы в текущей очереди на этом компьютере.");
    }
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
        if (_section == "queue") ShowQueue();
        SetStatus(next ? "Трек будет играть следующим" : "Трек добавлен в конец очереди"); UpdateControls();
    }
    private int SelectedQueueIndex()
    {
        if (TracksList.SelectedItem is not TrackRow selected) return -1;
        var ordinal = _view.Cast<TrackRow>().Take(TracksList.SelectedIndex + 1).Count(row => ReferenceEquals(row, selected));
        for (var i = 0; i < _queue.Count; i++) if (ReferenceEquals(_queue[i], selected) && --ordinal == 0) return i;
        return -1;
    }
    private async void RemoveQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_section != "queue") return;
        var index = SelectedQueueIndex(); if (index < 0) return;
        var removingCurrent = index == _playbackQueue.CurrentIndex;
        var next = _playbackQueue.RemoveAt(index);
        ShowQueue();
        if (removingCurrent)
        {
            if (next is not null) await PlayTrackAsync(next);
            else
            {
                _playbackGeneration++; _playCts?.Cancel(); ReportWaveEnd(false); _player.Stop(); _progressTimer.Stop();
                if (_current is not null) _current.IsPlaying = false;
                _current = null; HighlightTrack(null); TrackTitle.Text = "Очередь пуста"; TrackArtist.Text = "Добавьте треки в очередь"; QualityText.Text = "";
            }
        }
        UpdateControls(); UpdateProgress();
    }
    private void MoveQueueUp_Click(object sender, RoutedEventArgs e) => MoveSelectedQueue(-1);
    private void MoveQueueDown_Click(object sender, RoutedEventArgs e) => MoveSelectedQueue(1);
    private void MoveSelectedQueue(int direction)
    {
        if (_section != "queue") return;
        var index = SelectedQueueIndex(); var to = index + direction;
        if (index < 0 || to < 0 || to >= _queue.Count) return;
        _playbackQueue.Move(index, to); ShowQueue(); TracksList.SelectedIndex = to; UpdateControls();
    }
    private void UpdateQueueControls()
    {
        if (ShuffleButton is null) return;
        // Keep the compact transport controls icon-only; state belongs in colour,
        // the indicator and the accessible name, rather than clipped text.
        ShuffleButton.Foreground = (System.Windows.Media.Brush)FindResource(_playbackQueue.Shuffle ? "AccentBrush" : "MutedBrush");
        ShuffleIndicator.Visibility = _playbackQueue.Shuffle ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.ToolTip = _playbackQueue.Shuffle ? "Перемешивание включено" : "Включить перемешивание";
        System.Windows.Automation.AutomationProperties.SetName(ShuffleButton, (string)ShuffleButton.ToolTip);
        RepeatButton.Foreground = (System.Windows.Media.Brush)FindResource(_playbackQueue.Repeat == RepeatMode.Off ? "MutedBrush" : "AccentBrush");
        RepeatIndicator.Visibility = _playbackQueue.Repeat == RepeatMode.Off ? Visibility.Collapsed : Visibility.Visible;
        RepeatOneLabel.Visibility = _playbackQueue.Repeat == RepeatMode.One ? Visibility.Visible : Visibility.Collapsed;
        RepeatButton.ToolTip = _playbackQueue.Repeat switch { RepeatMode.All => "Повтор всей очереди", RepeatMode.One => "Повтор трека", _ => "Повтор выключен" };
        System.Windows.Automation.AutomationProperties.SetName(RepeatButton, (string)RepeatButton.ToolTip);
    }
    private void RefreshPlaybackView()
    {
        if (_section == "queue") { ShowRows(_playbackQueue.Items); SelectVisibleQueueOccurrence(_playbackQueue.CurrentIndex); }
        else if (_section == "queue-history") ShowRows(_playbackQueue.History.Reverse());
    }
    private void SelectVisibleQueueOccurrence(int queueIndex)
    {
        TracksList.SelectedIndex = -1;
        if (queueIndex < 0 || queueIndex >= _queue.Count) return;
        var target = _queue[queueIndex];
        var ordinal = _queue.Take(queueIndex + 1).Count(row => ReferenceEquals(row, target));
        var visible = 0;
        foreach (TrackRow row in _view)
        {
            if (ReferenceEquals(row, target) && --ordinal == 0) { TracksList.SelectedIndex = visible; return; }
            visible++;
        }
    }
}
