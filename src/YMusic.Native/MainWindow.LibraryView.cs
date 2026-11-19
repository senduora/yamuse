using System.Windows;
using Microsoft.Win32.SafeHandles;
using System.Windows.Threading;
using OfflineCopyChecks = YMusic.Native.OfflineLibrary.OfflineCopyChecks;

namespace YMusic.Native;

public partial class MainWindow
{
    private const int BackgroundOfflineThreshold = 256;
    private CancellationTokenSource? _offlineMarksCts, _playlistStatusCts;
    private Task? _offlineMarksTask, _playlistStatusTask;
    private FileSystemWatcher? _queueOfflineWatcher;
    private DispatcherTimer? _queueOfflineWatcherTimer;
    private DispatcherTimer? _queueOfflineRootTimer;
    private UninstallFileIdentity? _queueOfflineRootIdentity;
    internal Func<bool>? QueueOfflineRootIdentityUnavailableForChecks { get; set; }
    private int _offlineViewEpoch;
    private PlaylistStatusSnapshot? _playlistStatusCache, _playlistStatusPending;
    private sealed record OfflineMark(TrackRow Track, string? Path, string? Quality, bool HasQueueVariant = false);
    private sealed record PlaylistStatusSnapshot(string Owner, string Kind, int Revision, TrackRow[] Tracks, int Epoch,
        OfflineCopyChecks? Checks, OfflinePlaylistStatus? Status);

    private void WatchOpenQueueOfflineCopies()
    {
        if (_section != "queue" || !_offline.IsAvailable || !_tracks.Any(row => row.IsOfflineLibraryRow))
        { StopQueueOfflineWatcher(); return; }
        var firstWatch = _queueOfflineRootTimer is null;
        if (firstWatch)
        {
            var root = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _queueOfflineRootTimer = root;
            root.Tick += (_, _) =>
            {
                if (_closed || _section != "queue" || !ReferenceEquals(root, _queueOfflineRootTimer) || _offlineMaintenance) return;
                if (Directory.Exists(_offline.DirectoryPath))
                {
                    if (_queueOfflineWatcher is not null && ReadQueueOfflineRootIdentity() is { } current
                        && current != _queueOfflineRootIdentity)
                    {
                        StopQueueOfflineWatcher(stopRootTimer: false);
                        InvalidateOfflineViewState();
                    }
                    if (_queueOfflineWatcher is null) WatchOpenQueueOfflineCopies();
                }
                else if (_queueOfflineWatcher is not null)
                {
                    StopQueueOfflineWatcher(stopRootTimer: false);
                    ReadOpenQueueOfflineMarks();
                }
            };
            root.Start();
        }
        if (_queueOfflineWatcher is not null) return;
        if (!Directory.Exists(_offline.DirectoryPath))
        {
            if (firstWatch && !_offlineMaintenance) ReadOpenQueueOfflineMarks();
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(_offline.DirectoryPath)
            { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
            _queueOfflineWatcher = watcher;
            _queueOfflineWatcherTimer = timer;
            var queued = 0;
            var rearmRequested = 0;
            void RequestRead()
            {
                if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(watcher, _queueOfflineWatcher)) timer.Start();
                }, DispatcherPriority.Background);
            }
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Interlocked.Exchange(ref queued, 0);
                if (_closed || _offlineMaintenance || _section != "queue" || !ReferenceEquals(watcher, _queueOfflineWatcher)) return;
                if (Interlocked.Exchange(ref rearmRequested, 0) != 0)
                {
                    StopQueueOfflineWatcher(stopRootTimer: false);
                    InvalidateOfflineViewState();
                    if (Directory.Exists(_offline.DirectoryPath)) WatchOpenQueueOfflineCopies();
                    else ReadOpenQueueOfflineMarks();
                    return;
                }
                ReadOpenQueueOfflineMarks();
                if (!Directory.Exists(_offline.DirectoryPath))
                    StopQueueOfflineWatcher(stopRootTimer: false);
            };
            watcher.Created += (_, _) => RequestRead();
            watcher.Changed += (_, _) => RequestRead();
            watcher.Deleted += (_, _) => RequestRead();
            watcher.Renamed += (_, _) => RequestRead();
            watcher.Error += (_, _) => { Interlocked.Exchange(ref rearmRequested, 1); RequestRead(); };
            // Keep the inspected root name bound until the watcher has opened it.
            using (var root = TryHoldQueueOfflineRoot())
            {
                _queueOfflineRootIdentity = ReadQueueOfflineRootIdentity(root);
                watcher.EnableRaisingEvents = true;
            }
            // Recheck changes between the initial row read and subscription.
            RequestRead();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StopQueueOfflineWatcher(stopRootTimer: false);
            _diagnostics.LastErrorType = "OfflineWatch:" + error.GetType().Name;
        }
    }

    private UninstallFileIdentity? ReadQueueOfflineRootIdentity()
    {
        using var handle = TryHoldQueueOfflineRoot();
        return ReadQueueOfflineRootIdentity(handle);
    }

    private SafeFileHandle? TryHoldQueueOfflineRoot()
    {
        try { return UninstallPathGuard.HoldDirectory(_offline.DirectoryPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private UninstallFileIdentity? ReadQueueOfflineRootIdentity(SafeFileHandle? handle)
    {
        if (handle is null || QueueOfflineRootIdentityUnavailableForChecks?.Invoke() == true) return null;
        try
        {
            return UninstallPathGuard.GetStableFileIdentity(handle);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private void ReadOpenQueueOfflineMarks()
    {
        var rows = _tracks.Where(row => row.IsOfflineLibraryRow).ToArray();
        if (rows.Length > 0) QueueOfflineMarks(rows);
    }

    private void StopQueueOfflineWatcher(bool stopRootTimer = true)
    {
        if (stopRootTimer)
        {
            _queueOfflineRootTimer?.Stop();
            _queueOfflineRootTimer = null;
        }
        var watcher = _queueOfflineWatcher;
        _queueOfflineWatcher = null;
        _queueOfflineRootIdentity = null;
        _queueOfflineWatcherTimer?.Stop();
        _queueOfflineWatcherTimer = null;
        watcher?.Dispose();
    }

    private void InvalidateOfflineViewState()
    {
        _offlineViewEpoch++;
        _offlineMarksCts?.Cancel(); _playlistStatusCts?.Cancel();
        _playlistStatusCache = _playlistStatusPending = null;
    }

    private void InvalidatePlaylistStatus()
    {
        _playlistStatusCts?.Cancel();
        _playlistStatusCache = _playlistStatusPending = null;
    }

    private OfflineMark[] ReadOfflineMarks(IReadOnlyList<TrackRow> tracks, CancellationToken token, OfflineCopyChecks? checks)
    {
        var marks = new OfflineMark[tracks.Count];
        for (var i = 0; i < tracks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var track = tracks[i];
            if (track.IsOfflineLibraryRow)
            {
                var file = ApplyOfflineCopyCheck(track, _offline.ResolvePlaybackSource(track, requireExactOfflineVariant: true), checks, allowAlternate: false);
                marks[i] = new(track, file?.Path, file?.Quality,
                    file is null && _offline.ResolvePlaybackSource(track) is { Bytes: not null });
                continue;
            }
            var candidate = _offline.FindCatalogPlaybackMark(track);
            var source = ApplyOfflineCopyCheck(track, candidate, checks, allowAlternate: true);
            marks[i] = new(track, source?.Path, source?.Quality, source is null && !track.Available && candidate is { Bytes: not null });
        }
        return marks;
    }

    private void ApplyOfflineMarks(IEnumerable<OfflineMark> marks)
    {
        foreach (var mark in marks)
        {
            var wasDownloaded = mark.Track.IsDownloaded;
            mark.Track.HasOfflineQueueVariant = mark.HasQueueVariant;
            mark.Track.DownloadedPath = mark.Path;
            mark.Track.DownloadedQuality = mark.Quality;
            if (wasDownloaded != mark.Track.IsDownloaded) QueueDownloadedSortRefresh();
        }
    }

    private void QueueOfflineMarks(TrackRow[] tracks)
    {
        // Entering a large view paints cached row marks immediately, then
        // revalidates disk state off the dispatcher even after an external edit.
        _playlistStatusCache = null; _playlistStatusCts?.Cancel(); _playlistStatusPending = null;
        _offlineMarksCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _offlineMarksCts = cts;
        var epoch = _offlineViewEpoch; var account = _accountGeneration;
        var checks = CurrentOfflineDownloadChecks;
        _offlineMarksTask = RunAsync();
        async Task RunAsync()
        {
            try
            {
                var marks = await Task.Run(() => ReadOfflineMarks(tracks, cts.Token, checks), cts.Token);
                if (_closed || cts.IsCancellationRequested || epoch != _offlineViewEpoch || account != _accountGeneration || _offlineMaintenance) return;
                if (!ReferenceEquals(checks, CurrentOfflineDownloadChecks)) { QueueOfflineMarks(tracks); return; }
                ApplyOfflineMarks(marks);
                UpdateControls();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
            {
                if (!_closed && !cts.IsCancellationRequested && epoch == _offlineViewEpoch && account == _accountGeneration)
                    _diagnostics.LastErrorType = "OfflineMarks:" + error.GetType().Name;
            }
            finally { if (ReferenceEquals(_offlineMarksCts, cts)) _offlineMarksCts = null; cts.Dispose(); }
        }
    }

    private static bool SamePlaylistStatus(PlaylistStatusSnapshot? snapshot, PlaylistDetails playlist, int epoch, OfflineCopyChecks? checks)
        => snapshot is not null && snapshot.Epoch == epoch && snapshot.Owner == playlist.Owner && snapshot.Kind == playlist.Kind
            && snapshot.Revision == playlist.Revision && snapshot.Tracks.Length == playlist.Tracks.Count
            && ReferenceEquals(snapshot.Checks, checks)
            && snapshot.Tracks.Zip(playlist.Tracks).All(pair => ReferenceEquals(pair.First, pair.Second));

    private OfflinePlaylistStatus? LargePlaylistStatus(PlaylistDetails playlist)
    {
        var checks = CurrentOfflineDownloadChecks;
        if (SamePlaylistStatus(_playlistStatusCache, playlist, _offlineViewEpoch, checks)) return _playlistStatusCache!.Status;
        if (SamePlaylistStatus(_playlistStatusPending, playlist, _offlineViewEpoch, checks)) return null;
        _playlistStatusCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _playlistStatusCts = cts;
        var snapshot = new PlaylistStatusSnapshot(playlist.Owner, playlist.Kind, playlist.Revision, playlist.Tracks.ToArray(), _offlineViewEpoch, checks, null);
        _playlistStatusPending = snapshot;
        var account = _accountGeneration;
        _playlistStatusTask = RunAsync();
        return null;
        async Task RunAsync()
        {
            try
            {
                var state = await Task.Run(() => _offline.InspectPlaylistStatus(snapshot.Owner, snapshot.Kind, snapshot.Tracks, cts.Token, snapshot.Checks), cts.Token);
                if (_closed || cts.IsCancellationRequested || account != _accountGeneration || snapshot.Epoch != _offlineViewEpoch
                    || !ReferenceEquals(_playlistStatusPending, snapshot)) return;
                if (!ReferenceEquals(snapshot.Checks, CurrentOfflineDownloadChecks))
                {
                    _playlistStatusPending = null;
                    UpdatePlaylistDownloadedStatus();
                    return;
                }
                _playlistStatusCache = snapshot with { Status = state };
                _playlistStatusPending = null;
                UpdatePlaylistDownloadedStatus();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
            {
                if (!_closed && !cts.IsCancellationRequested && account == _accountGeneration && snapshot.Epoch == _offlineViewEpoch
                    && ReferenceEquals(_playlistStatusPending, snapshot) && ReferenceEquals(snapshot.Checks, CurrentOfflineDownloadChecks))
                { _diagnostics.LastErrorType = "PlaylistStatus:" + error.GetType().Name; PlaylistDownloadText.Text = "Не удалось прочитать статус скачивания"; }
            }
            finally
            {
                if (ReferenceEquals(_playlistStatusPending, snapshot)) _playlistStatusPending = null;
                if (ReferenceEquals(_playlistStatusCts, cts)) _playlistStatusCts = null;
                cts.Dispose();
            }
        }
    }
}
