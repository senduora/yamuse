using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace YMusic.Native;

internal enum DownloadEntryState { Queued, Resolving, Downloading, Completed, Failed, Unavailable, Paused, Suspended, Removed }
internal enum DownloadQueueAppendResult { Unchanged, Added, Rejected }
internal sealed record DeferredFavoriteState(DownloadEntryState PreviousState, AudioQuality? PreviousQuality,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PlaybackTrackState? PreviousTrack = null);

internal sealed class DownloadEntry(TrackRow track, AudioQuality? automaticFavoriteQuality = null) : INotifyPropertyChanged
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    internal TrackRow Track { get; private set; } = track;
    internal AudioQuality? AutomaticFavoriteQuality { get; private set; } = automaticFavoriteQuality;
    internal DeferredFavoriteState? DeferredFavorite { get; private set; }
    internal bool ResumeAutomaticallyOnStartup { get; private set; }
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public DownloadEntryState State { get; private set; } = DownloadEntryState.Queued;
    public long Bytes { get; private set; }
    public long? TotalBytes { get; private set; }
    public string Error { get; private set; } = "";
    public int Attempts { get; private set; }
    public bool RemovedFromCollection { get; private set; }
    public bool SkipFavoritesDownload { get; private set; }
    public string CollectionText => RemovedFromCollection ? "Убран из «Мне нравится»" : "";
    public bool Active => State is DownloadEntryState.Resolving or DownloadEntryState.Downloading;
    public bool Indeterminate => Active && TotalBytes is not > 0;
    public double Percent => State == DownloadEntryState.Completed ? 100 : TotalBytes is > 0 ? Math.Clamp(100.0 * Bytes / TotalBytes.Value, 0, 100) : 0;
    public string SizeText => TotalBytes is > 0 ? $"{FormatBytes(Bytes)} / {FormatBytes(TotalBytes.Value)}" : Bytes > 0 ? $"{FormatBytes(Bytes)} / неизвестен" : "Размер неизвестен";
    public string StatusText => State switch
    {
        DownloadEntryState.Queued => "В очереди", DownloadEntryState.Resolving => "Получаем аудио",
        DownloadEntryState.Downloading => "Скачивается", DownloadEntryState.Completed => "Скачан",
        DownloadEntryState.Failed => "Ошибка", DownloadEntryState.Unavailable => "Недоступен",
        DownloadEntryState.Paused => "Пауза", DownloadEntryState.Suspended => "Остановлен",
        DownloadEntryState.Removed => "Удалён", _ => ""
    };
    internal void SetState(DownloadEntryState state, string error = "", bool resumeAutomaticallyOnStartup = false)
    {
        VerifyAccess();
        if (state == DownloadEntryState.Removed) { Bytes = 0; TotalBytes = null; }
        if (state is not (DownloadEntryState.Queued or DownloadEntryState.Paused or DownloadEntryState.Suspended)) DeferredFavorite = null;
        State = state; Error = error;
        ResumeAutomaticallyOnStartup = state == DownloadEntryState.Paused && resumeAutomaticallyOnStartup;
        Changed();
    }
    internal void Begin()
    { VerifyAccess(); Attempts = Math.Min(Attempts + 1, DownloadQueueStore.MaximumAttempts); Bytes = 0; TotalBytes = null; SetState(DownloadEntryState.Resolving); }
    internal void RequeueAutomaticFavorite(TrackRow currentTrack, AudioQuality quality)
    {
        VerifyAccess();
        if (State is DownloadEntryState.Removed or DownloadEntryState.Unavailable)
            DeferredFavorite = new(State, AutomaticFavoriteQuality, DownloadQueueStore.ToSavedTrack(Track));
        Track = currentTrack; AutomaticFavoriteQuality = quality;
        Bytes = 0; TotalBytes = null; SetState(DownloadEntryState.Queued);
    }
    internal void DiscardDeferredFavorite(bool favoritesBatch)
    {
        VerifyAccess();
        if (DeferredFavorite is not { } deferred) return;
        if (deferred.PreviousTrack is { } previous) Track = DownloadQueueStore.ToTrackRow(previous, Track.Number);
        AutomaticFavoriteQuality = deferred.PreviousQuality;
        SetCollectionLike(!RemovedFromCollection, favoritesBatch || AutomaticFavoriteQuality is not null);
        SetState(deferred.PreviousState, deferred.PreviousState == DownloadEntryState.Unavailable ? DownloadFailure.UnavailableReason : "");
    }
    internal void Progress(AudioProgress progress)
    {
        VerifyAccess();
        if (!Active) return;
        Bytes = Math.Max(Bytes, Math.Max(0, progress.Bytes));
        TotalBytes = progress.Total is > 0 ? Math.Max(Bytes, progress.Total.Value) : null;
        State = DownloadEntryState.Downloading; Changed();
    }
    internal void Complete(long bytes)
    { VerifyAccess(); Bytes = Math.Max(0, bytes); TotalBytes = Bytes > 0 ? Bytes : null; SetState(DownloadEntryState.Completed); }
    internal void Restore(DownloadEntryState state, long bytes, int attempts, string error, bool removedFromCollection,
        bool skipFavoritesDownload, DeferredFavoriteState? deferredFavorite = null, bool resumeAutomaticallyOnStartup = false)
    {
        VerifyAccess(); Bytes = Math.Max(0, bytes); TotalBytes = state == DownloadEntryState.Completed && Bytes > 0 ? Bytes : null;
        Attempts = attempts; RemovedFromCollection = removedFromCollection; SkipFavoritesDownload = skipFavoritesDownload;
        DeferredFavorite = deferredFavorite;
        SetState(state, DownloadFailure.RestorableReason(error), resumeAutomaticallyOnStartup);
    }
    internal bool SetCollectionLike(bool liked, bool favoritesBatch)
    {
        VerifyAccess();
        var removed = !liked;
        var skip = removed && favoritesBatch;
        if (RemovedFromCollection == removed && SkipFavoritesDownload == skip) return false;
        RemovedFromCollection = removed; SkipFavoritesDownload = skip; Changed();
        return true;
    }
    internal static string FormatBytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:F2} ГБ" : bytes >= 1048576 ? $"{bytes / 1048576.0:F1} МБ" : bytes >= 1024 ? $"{bytes / 1024.0:F1} КБ" : $"{bytes} Б";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void VerifyAccess()
    { if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("Download entry mutations require their owning dispatcher thread."); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

// All queue transitions belong to the window's dispatcher. The streaming progress
// sink below is the only object a downloader may update from another thread.
internal sealed class DownloadQueueState
{
    internal const string EmptyLabel = "Очередь пуста";
    internal const string EmptyNotice = "Выберите треки или скачайте плейлист целиком.";
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    public ObservableCollection<DownloadEntry> Entries { get; } = [];
    public string Label { get; private set; } = EmptyLabel;
    public string Notice { get; private set; } = EmptyNotice;
    public bool Running { get; private set; }
    public bool Suspended { get; private set; }
    public bool FavoritesBatch { get; private set; }
    internal event Action? StateChanged;
    public bool CanResume => !Running && !Suspended && Entries.Any(e => !e.SkipFavoritesDownload && (e.State is DownloadEntryState.Queued or DownloadEntryState.Paused));
    public bool CanRetry => !Running && !Suspended && Entries.Any(e => !e.SkipFavoritesDownload && e.State == DownloadEntryState.Failed);
    public bool HasPaused => Entries.Any(e => !e.SkipFavoritesDownload && (e.State is DownloadEntryState.Paused or DownloadEntryState.Suspended));
    public string Summary
    {
        get
        {
            var removed = Entries.Count(entry => entry.State == DownloadEntryState.Removed);
            var retained = Entries.Count - removed;
            var summary = retained == 0 ? Label : $"{Label} · сохранено {Entries.Count(e => e.State == DownloadEntryState.Completed)}/{retained} · активно {Entries.Count(e => e.Active)} · ошибок {Entries.Count(e => e.State == DownloadEntryState.Failed && !e.SkipFavoritesDownload)} · {DownloadEntry.FormatBytes(Entries.Sum(e => e.Bytes))}";
            var excluded = Entries.Count(entry => entry.SkipFavoritesDownload);
            if (excluded > 0) summary += $" · убрано из коллекции {excluded}";
            if (removed > 0) summary += $" · удалено {removed}";
            return summary;
        }
    }
    internal void Replace(IEnumerable<TrackRow> tracks, string label, bool favoritesBatch = false)
    {
        VerifyAccess();
        if (Running) throw new InvalidOperationException("A running download queue cannot be replaced.");
        var replaced = Entries.Count > 0;
        ClearEntries();
        foreach (var track in SelectWorkTracks(tracks)) AddEntry(new(track));
        Label = label; Suspended = false; FavoritesBatch = favoritesBatch;
        Notice = replaced ? "Показана новая очередь вместо предыдущей. Сохранённые файлы остаются в скачанном." : "Пауза сохраняет готовые треки. Незавершённый трек при продолжении скачивается заново.";
        StateChanged?.Invoke();
    }
    internal static IReadOnlyList<TrackRow> SelectWorkTracks(IEnumerable<TrackRow> tracks, int limit = int.MaxValue)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new List<TrackRow>();
        foreach (var track in tracks)
        {
            if (positions.TryAdd(track.Id, selected.Count))
            {
                selected.Add(track);
                if (selected.Count >= limit) break;
            }
            else if (track.Available && !selected[positions[track.Id]].Available)
                selected[positions[track.Id]] = track;
        }
        return selected;
    }
    internal DownloadQueueAppendResult AppendAutomaticFavorites(IEnumerable<TrackRow> tracks, AudioQuality quality,
        ICollection<TrackRow>? completedCandidates = null, Func<IEnumerable<DownloadEntry>, bool>? admit = null, bool requeueExisting = false)
    {
        VerifyAccess();
        var byId = Entries.ToDictionary(entry => entry.Track.Id, StringComparer.Ordinal);
        var added = new List<DownloadEntry>();
        var requeued = new Dictionary<DownloadEntry, TrackRow>();
        var completed = new List<TrackRow>();
        foreach (var track in SelectWorkTracks(tracks, DownloadQueueStore.MaximumEntries + 1))
        {
            if (!byId.TryGetValue(track.Id, out var entry))
            {
                added.Add(new(track, quality));
                if (Entries.Count + added.Count > DownloadQueueStore.MaximumEntries)
                    return DownloadQueueAppendResult.Rejected;
                continue;
            }
            if (entry.SkipFavoritesDownload || !track.Available) continue;
            if (completedCandidates is not null && entry.State == DownloadEntryState.Completed) completed.Add(track);
            if (requeueExisting && entry.State is (DownloadEntryState.Removed or DownloadEntryState.Unavailable))
                requeued.Add(entry, track);
        }
        var changed = added.Count > 0 || requeued.Count > 0;
        if (changed && !CanAdmitAutomaticChanges(added, requeued, quality, admit)) return DownloadQueueAppendResult.Rejected;
        foreach (var entry in added) AddEntry(entry);
        foreach (var (entry, track) in requeued) entry.RequeueAutomaticFavorite(track, quality);
        if (completedCandidates is not null)
            foreach (var track in completed) completedCandidates.Add(track);
        if (changed) StateChanged?.Invoke();
        return changed ? DownloadQueueAppendResult.Added : DownloadQueueAppendResult.Unchanged;
    }
    internal DownloadEntry[] RequeueMissingCompletedFavorites(IReadOnlyList<TrackRow> missing, AudioQuality quality,
        Func<IEnumerable<DownloadEntry>, bool> admit, out bool rejected)
    {
        VerifyAccess();
        rejected = false;
        if (missing.Count == 0) return [];
        var byId = Entries.ToDictionary(entry => entry.Track.Id, StringComparer.Ordinal);
        var requeued = new Dictionary<DownloadEntry, TrackRow>();
        foreach (var track in missing.DistinctBy(track => track.Id, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(track.Id, out var entry) || entry.State != DownloadEntryState.Completed || entry.SkipFavoritesDownload) continue;
            requeued.Add(entry, track);
        }
        if (requeued.Count == 0) return [];
        rejected = !CanAdmitAutomaticChanges([], requeued, quality, admit);
        if (rejected) return [];
        foreach (var (entry, track) in requeued) entry.RequeueAutomaticFavorite(track, quality);
        Running = true;
        StateChanged?.Invoke();
        return requeued.Keys.ToArray();
    }

    private bool CanAdmitAutomaticChanges(IEnumerable<DownloadEntry> added, IReadOnlyDictionary<DownloadEntry, TrackRow> requeued,
        AudioQuality quality, Func<IEnumerable<DownloadEntry>, bool>? admit)
    {
        return admit is null || admit(Entries.Select(Project).Concat(added));

        DownloadEntry Project(DownloadEntry entry)
        {
            if (!requeued.TryGetValue(entry, out var track)) return entry;
            var candidate = new DownloadEntry(entry.Track, entry.AutomaticFavoriteQuality);
            candidate.SetState(entry.State);
            candidate.RequeueAutomaticFavorite(track, quality);
            return candidate;
        }
    }
    internal bool DiscardUnstartedAutomaticFavorites()
    {
        VerifyAccess();
        var pending = Entries.Where(entry => entry.AutomaticFavoriteQuality is not null && (entry.Attempts == 0 || entry.DeferredFavorite is not null)
            && entry.State is DownloadEntryState.Queued or DownloadEntryState.Paused).ToArray();
        foreach (var entry in pending)
        {
            if (entry.DeferredFavorite is not null)
            {
                entry.DiscardDeferredFavorite(FavoritesBatch);
                continue;
            }
            entry.PropertyChanged -= EntryChanged;
            entry.SetState(DownloadEntryState.Removed);
            Entries.Remove(entry);
        }
        if (pending.Length > 0) StateChanged?.Invoke();
        return pending.Length > 0;
    }
    internal void Restore(RestoredDownloadQueue restored)
    {
        VerifyAccess();
        if (Running) throw new InvalidOperationException("A running download queue cannot be restored.");
        ClearEntries();
        foreach (var item in restored.Entries)
        {
            var entry = new DownloadEntry(item.Track, item.AutomaticFavoriteQuality);
            entry.Restore(item.State, item.Bytes, item.Attempts, item.Error, item.RemovedFromCollection,
                item.SkipFavoritesDownload, item.DeferredFavorite, item.ResumeAutomaticallyOnStartup);
            AddEntry(entry);
        }
        Label = restored.Label; FavoritesBatch = restored.FavoritesBatch; Suspended = false;
        Notice = CanResume || CanRetry
            ? "Очередь восстановлена на паузе. Нажмите «Продолжить»; готовые файлы не скачиваются повторно. Ошибки можно повторить отдельно."
            : "История загрузок восстановлена. Незавершённых загрузок нет.";
        StateChanged?.Invoke();
    }
    private void ClearEntries()
    { foreach (var entry in Entries) entry.PropertyChanged -= EntryChanged; Entries.Clear(); }
    private void AddEntry(DownloadEntry entry)
    { Entries.Add(entry); entry.PropertyChanged += EntryChanged; }
    private void EntryChanged(object? sender, PropertyChangedEventArgs args) => StateChanged?.Invoke();
    internal DownloadEntry[] BeginRun(bool retryFailed, IReadOnlySet<DownloadEntry>? resumeEntries = null)
    {
        VerifyAccess();
        if (Running || Suspended) return [];
        var selected = Entries.Where(e => !e.SkipFavoritesDownload && (resumeEntries is null || resumeEntries.Contains(e))
            && (retryFailed ? e.State == DownloadEntryState.Failed : e.State is DownloadEntryState.Queued or DownloadEntryState.Paused)).ToArray();
        if (selected.Length > 0 && retryFailed) Notice = "Повторяем только треки с ошибкой. Сохранённые и недоступные треки не скачиваются повторно.";
        else if (selected.Any(e => e.State == DownloadEntryState.Paused)) Notice = "Продолжаем очередь. Уже сохранённые треки не скачиваются повторно.";
        foreach (var entry in selected) entry.SetState(DownloadEntryState.Queued);
        Running = selected.Length > 0;
        StateChanged?.Invoke();
        return selected;
    }
    internal void Pause(bool allowAutomaticResume = false, bool automaticBatch = false)
    {
        VerifyAccess();
        foreach (var entry in Entries.Where(e => e.Active || e.State == DownloadEntryState.Queued))
            entry.SetState(DownloadEntryState.Paused, resumeAutomaticallyOnStartup:
                allowAutomaticResume && (automaticBatch || entry.AutomaticFavoriteQuality is not null));
        Notice = "Очередь на паузе. Нажмите «Продолжить»: сохранённые треки не скачиваются повторно.";
        StateChanged?.Invoke();
    }
    internal void KeepPausedAfterRestart()
    {
        VerifyAccess();
        foreach (var entry in Entries.Where(entry => entry.ResumeAutomaticallyOnStartup))
            entry.SetState(DownloadEntryState.Paused);
    }
    internal void Suspend()
    {
        VerifyAccess(); Suspended = true;
        foreach (var entry in Entries.Where(e => e.Active || e.State is DownloadEntryState.Queued or DownloadEntryState.Paused)) entry.SetState(DownloadEntryState.Suspended);
        Notice = "Очередь остановлена после выхода или изменения скачанного. Новую загрузку запустите из списка треков.";
        StateChanged?.Invoke();
    }
    internal void EndRun(bool interrupted)
    {
        VerifyAccess();
        if (interrupted && !Suspended) Pause();
        Running = false;
        if (!Suspended)
        {
            if (HasPaused) Notice = "Очередь на паузе. Нажмите «Продолжить»: сохранённые треки не скачиваются повторно.";
            else if (Entries.Any(e => e.State == DownloadEntryState.Failed && !e.SkipFavoritesDownload)) Notice = "Загрузка завершена с ошибками. Кнопка «Повторить неудачные» повторит только треки с ошибкой.";
            else if (Entries.Any(e => e.State == DownloadEntryState.Unavailable && !e.SkipFavoritesDownload)) Notice = "Доступные треки сохранены. Недоступные треки можно проверить после обновления каталога.";
            else if (Entries.Any(e => e.SkipFavoritesDownload)) Notice = "Треки, убранные из «Мне нравится», исключены из повторной загрузки.";
            else if (Entries.Any(e => e.State == DownloadEntryState.Removed)) Notice = "Удалённые треки больше не учитываются в загрузке.";
            else if (Entries.Count > 0) Notice = "Все треки очереди сохранены и доступны в разделе «Скачанное».";
        }
        StateChanged?.Invoke();
    }
    internal void Removed(string? trackId)
    {
        VerifyAccess();
        foreach (var entry in Entries.Where(e => trackId is null || e.Track.Id == trackId)) entry.SetState(DownloadEntryState.Removed);
    }
    internal void SetCollectionLike(string trackId, bool liked)
    {
        VerifyAccess();
        foreach (var entry in Entries.Where(e => e.Track.Id == trackId))
            entry.SetCollectionLike(liked, FavoritesBatch || entry.AutomaticFavoriteQuality is not null);
        if (!Running && !Suspended) EndRun(false);
    }
    internal bool ReconcileFavorites(IReadOnlySet<string> confirmedIds, Action<string> onReliked)
    {
        VerifyAccess();
        var changed = false;
        foreach (var entry in Entries)
        {
            if (!FavoritesBatch && entry.AutomaticFavoriteQuality is null) continue;
            var id = entry.Track.Id;
            var liked = confirmedIds.Contains(id);
            if (liked && entry.SkipFavoritesDownload) onReliked(id);
            changed |= entry.SetCollectionLike(liked, favoritesBatch: true);
        }
        if (changed && !Running && !Suspended) EndRun(false);
        return changed;
    }
    internal void AllowExplicitActionsAfterRemoval()
    {
        VerifyAccess();
        if (Running) throw new InvalidOperationException("A running download queue cannot be resumed after maintenance.");
        Suspended = false;
        foreach (var entry in Entries.Where(entry => entry.State == DownloadEntryState.Suspended)) entry.SetState(DownloadEntryState.Paused);
        EndRun(interrupted: false);
    }
    internal void VerifyAccess()
    { if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("Download queue mutations require their owning dispatcher thread."); }
}

internal sealed class DownloadProgressSink(Action? changed = null) : IProgress<AudioProgress>
{
    private AudioProgress? _latest;
    public void Report(AudioProgress value)
    { Interlocked.Exchange(ref _latest, value); changed?.Invoke(); }
    internal AudioProgress? Take() => Interlocked.Exchange(ref _latest, null);
}

internal static class DownloadFailure
{
    internal const string UnavailableReason = "Трек недоступен в каталоге. Обновите список и проверьте доступность в сервисе.";
    private const string UnknownReason = "Не удалось загрузить трек. Повторите; если ошибка сохранится, откройте диагностику.";
    // A snapshot can only carry reasons authored here, never arbitrary exception
    // messages, signed audio URLs, credentials or paths from an earlier process.
    // The v2 checkpoint uses these indices on disk. Append new reasons at the end.
    private static readonly string[] ReasonsByCode =
    [
        "", "Вход больше не действует. Войдите заново и запустите загрузку.",
        "Нет доступа к папке скачанного. Проверьте права и свободное место.",
        "На диске закончилось свободное место. Освободите место и повторите.",
        "Не удалось сохранить файл. Проверьте папку скачанного, свободное место и доступ к диску.",
        "Сервер не ответил вовремя. Проверьте сеть и повторите загрузку.",
        "Сервер отклонил доступ к аудио. Проверьте вход, подписку и доступность трека.",
        "Ошибка соединения с сервером. Проверьте сеть и повторите загрузку.",
        "Не удалось безопасно обновить скачанное. Проверьте папку и индекс библиотеки; файлы сохранены.",
        "Трек превышает допустимый размер 256 МБ.",
        "Аудиофайл получен не полностью или не прошёл проверку. Повторите загрузку.",
        "Сервис не выдал полное аудио. Проверьте вход, подписку и доступность трека.",
        "Аудиосервер временно отклонил запрос. Повторите загрузку позже.",
        "Не удалось получить безопасный полный аудиофайл от сервиса. Повторите загрузку позже.",
        "Трек недоступен в каталоге. Обновите список и проверьте доступность в сервисе.",
        UnknownReason
    ];
    private static readonly HashSet<string> RestorableReasons = new(ReasonsByCode, StringComparer.Ordinal);
    internal static string RestorableReason(string reason) => RestorableReasons.Contains(reason) ? reason : UnknownReason;
    internal static string HighestCheckpointReason => ReasonsByCode[^1];
    internal static int CheckpointReasonCode(string reason)
    {
        var code = Array.IndexOf(ReasonsByCode, reason);
        return code >= 0 ? code : throw new JsonException("Неизвестная причина ошибки загрузки.");
    }
    internal static string CheckpointReasonText(int code) => code >= 0 && code < ReasonsByCode.Length
        ? ReasonsByCode[code] : throw new JsonException("Неизвестный код ошибки загрузки.");
    // Never show Exception.Message: HTTP errors and paths can contain signed URLs,
    // tokens or private directories. Only locally authored reasons reach the UI.
    internal static string Describe(Exception error) => error switch
    {
        MusicException { Authentication: true } => "Вход больше не действует. Войдите заново и запустите загрузку.",
        UnauthorizedAccessException => "Нет доступа к папке скачанного. Проверьте права и свободное место.",
        IOException io when (io.HResult & 0xffff) is 0x70 or 0x27 => "На диске закончилось свободное место. Освободите место и повторите.",
        IOException => "Не удалось сохранить файл. Проверьте папку скачанного, свободное место и доступ к диску.",
        OperationCanceledException or TimeoutException => "Сервер не ответил вовремя. Проверьте сеть и повторите загрузку.",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "Сервер отклонил доступ к аудио. Проверьте вход, подписку и доступность трека.",
        HttpRequestException => "Ошибка соединения с сервером. Проверьте сеть и повторите загрузку.",
        MusicException music => MusicReason(music.Message),
        _ => UnknownReason
    };
    private static string MusicReason(string message)
    {
        if (message.Contains("индекс", StringComparison.OrdinalIgnoreCase) || message.Contains("папк", StringComparison.OrdinalIgnoreCase) || message.Contains("путь", StringComparison.OrdinalIgnoreCase))
            return "Не удалось безопасно обновить скачанное. Проверьте папку и индекс библиотеки; файлы сохранены.";
        if (message.Contains("256 МБ", StringComparison.Ordinal)) return "Трек превышает допустимый размер 256 МБ.";
        if (message.Contains("не полностью", StringComparison.Ordinal) || message.Contains("часть файла", StringComparison.Ordinal) || message.Contains("контрольная сумма", StringComparison.Ordinal))
            return "Аудиофайл получен не полностью или не прошёл проверку. Повторите загрузку.";
        if (message.Contains("доступ", StringComparison.OrdinalIgnoreCase) || message.Contains("HTTP 403", StringComparison.Ordinal) || message.Contains("HTTP 401", StringComparison.Ordinal))
            return "Сервис не выдал полное аудио. Проверьте вход, подписку и доступность трека.";
        if (message.Contains("HTTP", StringComparison.Ordinal)) return "Аудиосервер временно отклонил запрос. Повторите загрузку позже.";
        return "Не удалось получить безопасный полный аудиофайл от сервиса. Повторите загрузку позже.";
    }
}
