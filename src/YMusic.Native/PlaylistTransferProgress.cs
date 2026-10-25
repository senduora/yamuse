namespace YMusic.Native;

// Queue work is measured by tracks, not by an invented total download size.
// Failures finish work but remain explicitly distinct from saved audio.
internal sealed record PlaylistTransferProgress(int Total, int Completed, int Failed, int Unavailable, int Active,
    int Finished, long ReceivedBytes, double Percent)
{
    internal static PlaylistTransferProgress Read(IEnumerable<DownloadEntry> source)
    {
        int total = 0, completed = 0, failed = 0, unavailable = 0, active = 0;
        long bytes = 0; double partial = 0;
        foreach (var entry in source)
        {
            if (entry.SkipFavoritesDownload || entry.State == DownloadEntryState.Removed) continue;
            total++; bytes += entry.Bytes;
            if (entry.State == DownloadEntryState.Completed) completed++;
            if (entry.State == DownloadEntryState.Failed) failed++;
            if (entry.State == DownloadEntryState.Unavailable) unavailable++;
            if (entry.Active) { active++; partial += entry.Percent / 100.0; }
        }
        var finished = completed + failed + unavailable;
        return new(total, completed, failed, unavailable, active, finished, bytes,
            total == 0 ? 0 : Math.Clamp(100.0 * (finished + partial) / total, 0, 100));
    }
    internal string Details => $"Скачано {Completed:N0} из {Total:N0} · в работе {Active}" +
        (Failed > 0 ? $" · ошибок {Failed}" : "") + (Unavailable > 0 ? $" · недоступно {Unavailable}" : "") +
        $" · получено {DownloadEntry.FormatBytes(ReceivedBytes)}";
}
