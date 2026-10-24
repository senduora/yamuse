using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Http;

namespace YMusic.Native;

internal enum DownloadEntryState { Queued, Resolving, Downloading, Completed, Failed, Unavailable, Paused, Suspended, Removed }

internal sealed class DownloadEntry(TrackRow track) : INotifyPropertyChanged
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    internal TrackRow Track { get; } = track;
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
    internal void SetState(DownloadEntryState state, string error = "")
    { VerifyAccess(); State = state; Error = error; Changed(); }
    internal void Begin()
    { VerifyAccess(); Attempts++; Bytes = 0; TotalBytes = null; SetState(DownloadEntryState.Resolving); }
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
    internal void SetCollectionLike(bool liked, bool favoritesBatch)
    { VerifyAccess(); RemovedFromCollection = !liked; SkipFavoritesDownload = !liked && favoritesBatch; Changed(); }
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
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    public ObservableCollection<DownloadEntry> Entries { get; } = [];
    public string Label { get; private set; } = "Очередь пуста";
    public string Notice { get; private set; } = "Выберите треки или скачайте плейлист целиком.";
    public bool Running { get; private set; }
    public bool Suspended { get; private set; }
    public bool FavoritesBatch { get; private set; }
    public bool CanResume => !Running && !Suspended && Entries.Any(e => !e.SkipFavoritesDownload && (e.State is DownloadEntryState.Queued or DownloadEntryState.Paused));
    public bool CanRetry => !Running && !Suspended && Entries.Any(e => !e.SkipFavoritesDownload && e.State == DownloadEntryState.Failed);
    public bool HasPaused => Entries.Any(e => !e.SkipFavoritesDownload && (e.State is DownloadEntryState.Paused or DownloadEntryState.Suspended));
    public string Summary => Entries.Count == 0 ? Label : $"{Label} · сохранено {Entries.Count(e => e.State == DownloadEntryState.Completed)}/{Entries.Count} · активно {Entries.Count(e => e.Active)} · ошибок {Entries.Count(e => e.State == DownloadEntryState.Failed && !e.SkipFavoritesDownload)} · {DownloadEntry.FormatBytes(Entries.Sum(e => e.Bytes))}" + (Entries.Any(e => e.SkipFavoritesDownload) ? $" · убрано из коллекции {Entries.Count(e => e.SkipFavoritesDownload)}" : "");
    internal void Replace(IEnumerable<TrackRow> tracks, string label, bool favoritesBatch = false)
    {
        VerifyAccess();
        if (Running) throw new InvalidOperationException("A running download queue cannot be replaced.");
        var replaced = Entries.Count > 0;
        Entries.Clear();
        foreach (var track in tracks.DistinctBy(t => t.Id, StringComparer.Ordinal)) Entries.Add(new(track));
        Label = label; Suspended = false; FavoritesBatch = favoritesBatch;
        Notice = replaced ? "Показана новая очередь вместо предыдущей. Сохранённые файлы остаются в скачанном." : "Пауза сохраняет готовые треки. Незавершённый трек при продолжении скачивается заново.";
    }
    internal DownloadEntry[] BeginRun(bool retryFailed)
    {
        VerifyAccess();
        if (Running || Suspended) return [];
        var selected = Entries.Where(e => !e.SkipFavoritesDownload && (retryFailed ? e.State == DownloadEntryState.Failed : e.State is DownloadEntryState.Queued or DownloadEntryState.Paused)).ToArray();
        if (selected.Length > 0 && retryFailed) Notice = "Повторяем только треки с ошибкой. Сохранённые и недоступные треки не скачиваются повторно.";
        else if (selected.Any(e => e.State == DownloadEntryState.Paused)) Notice = "Продолжаем очередь. Уже сохранённые треки не скачиваются повторно.";
        foreach (var entry in selected) entry.SetState(DownloadEntryState.Queued);
        Running = selected.Length > 0;
        return selected;
    }
    internal void Pause()
    {
        VerifyAccess();
        foreach (var entry in Entries.Where(e => e.Active || e.State == DownloadEntryState.Queued)) entry.SetState(DownloadEntryState.Paused);
        Notice = "Очередь на паузе. Нажмите «Продолжить»: сохранённые треки не скачиваются повторно.";
    }
    internal void Suspend()
    {
        VerifyAccess(); Suspended = true;
        foreach (var entry in Entries.Where(e => e.Active || e.State is DownloadEntryState.Queued or DownloadEntryState.Paused)) entry.SetState(DownloadEntryState.Suspended);
        Notice = "Очередь остановлена после выхода или изменения скачанного. Новую загрузку запустите из списка треков.";
    }
    internal void EndRun(bool interrupted)
    {
        VerifyAccess();
        if (interrupted && !Suspended) Pause();
        Running = false;
        if (Suspended) return;
        if (HasPaused) Notice = "Очередь на паузе. Нажмите «Продолжить»: сохранённые треки не скачиваются повторно.";
        else if (Entries.Any(e => e.State == DownloadEntryState.Failed && !e.SkipFavoritesDownload)) Notice = "Загрузка завершена с ошибками. Кнопка «Повторить неудачные» повторит только треки с ошибкой.";
        else if (Entries.Any(e => e.State == DownloadEntryState.Unavailable && !e.SkipFavoritesDownload)) Notice = "Доступные треки сохранены. Недоступные треки можно проверить после обновления каталога.";
        else if (Entries.Any(e => e.SkipFavoritesDownload)) Notice = "Треки, убранные из «Мне нравится», исключены из повторной загрузки.";
        else if (Entries.Count > 0) Notice = "Все треки очереди сохранены и доступны в разделе «Скачанное».";
    }
    internal void Removed(string? trackId)
    {
        VerifyAccess();
        foreach (var entry in Entries.Where(e => trackId is null || e.Track.Id == trackId)) entry.SetState(DownloadEntryState.Removed);
    }
    internal void SetCollectionLike(string trackId, bool liked)
    {
        VerifyAccess();
        foreach (var entry in Entries.Where(e => e.Track.Id == trackId)) entry.SetCollectionLike(liked, FavoritesBatch);
        if (!Running && !Suspended) EndRun(false);
    }
    internal void VerifyAccess()
    { if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("Download queue mutations require their owning dispatcher thread."); }
}

internal sealed class DownloadProgressSink : IProgress<AudioProgress>
{
    private AudioProgress? _latest;
    public void Report(AudioProgress value) => Interlocked.Exchange(ref _latest, value);
    internal AudioProgress? Take() => Interlocked.Exchange(ref _latest, null);
}

internal static class DownloadFailure
{
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
        _ => "Не удалось загрузить трек. Повторите; если ошибка сохранится, откройте диагностику."
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
