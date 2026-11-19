namespace YMusic.Native;

internal static class OfflineAudioProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim Workers = new(4, 4);

    // Ownership passes back to the caller only after verification succeeds.
    internal static async Task<(BufferedAudioFile File, int? ConfirmedBitrate)> VerifyAsync(BufferedAudioFile candidate, AudioDescriptor audio, long durationMs,
        CancellationToken cancellationToken, Action? beforeOpen = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);
        Task<OfflineAudioEvidence?>? probe = null;
        var workerReserved = false;
        var verified = false;
        try
        {
            await Workers.WaitAsync(deadline.Token).ConfigureAwait(false);
            workerReserved = true;
            probe = Task.Run(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                beforeOpen?.Invoke();
                return LoudnessAnalysis.ProbeOfflineAudio(candidate.Path, audio.Codec, deadline.Token);
            });
            var observed = await probe.WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var confirmedBitrate = ConfirmAudioEvidence(observed, audio, durationMs);
            verified = true;
            return (candidate, confirmedBitrate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MusicException("Проверка скачанного аудио заняла слишком много времени.");
        }
        finally
        {
            if (probe is { IsCompleted: false }) _ = FinishAbandonedProbeAsync(probe, candidate, deadline);
            else
            {
                if (workerReserved) Workers.Release();
                deadline.Dispose();
                if (!verified) candidate.Dispose();
                if (probe is { IsFaulted: true }) _ = probe.Exception;
            }
        }
    }

    internal static int? ConfirmAudioEvidence(OfflineAudioEvidence? observed, AudioDescriptor audio, long durationMs)
    {
        if (observed is null)
            throw new MusicException("Загруженный файл не содержит поддерживаемой звуковой дорожки заявленного формата.");
        if (observed.ReadFailed)
            throw new MusicException("Не удалось прочитать аудиодорожку загруженного файла до конца.");
        if (durationMs > 60_000 && observed.Duration == TimeSpan.Zero)
            throw new MusicException("Не удалось подтвердить длительность скачанного аудио.");
        if (LooksLikePreview(durationMs, observed.Duration))
            throw new MusicException("Загруженный файл похож на фрагмент трека. Проверьте подписку.");
        if (observed.Duration > TimeSpan.Zero
            && durationMs - observed.Duration.TotalMilliseconds > Math.Clamp(durationMs * .03, 2000, 8000))
            throw new MusicException("Длительность скачанного аудио существенно меньше полной длительности трека.");
        if (audio.Codec == "mp3" && audio.Bitrate > 0 && !ConfirmsMp3Bitrate(audio.Bitrate, observed.Mp3))
            throw new MusicException("Не подтверждён заявленный битрейт скачанного MP3.");
        return observed.ConfirmedBitrate;
    }

    internal static bool LooksLikePreview(long durationMs, TimeSpan observed)
        => durationMs > 60_000 && observed.TotalSeconds is > 0 and <= 35;

    private static bool ConfirmsMp3Bitrate(int claimed, Mp3FrameEvidence? evidence)
    {
        if (evidence is null) return false;
        // VBR labels are rounded. CBR rates are exact and need no allowance.
        var tolerance = evidence.Variable ? Math.Clamp(evidence.KilobitsPerSecond * .03, 2, 8) : 0;
        return claimed <= evidence.KilobitsPerSecond + tolerance;
    }

    private static async Task FinishAbandonedProbeAsync(Task probe, BufferedAudioFile candidate, CancellationTokenSource deadline)
    {
        try { await probe.ConfigureAwait(false); }
        catch (Exception) { } // The caller has already received cancellation or timeout.
        finally
        {
            try { candidate.Dispose(); }
            finally { deadline.Dispose(); Workers.Release(); }
        }
    }
}
