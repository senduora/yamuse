using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal enum UpdateCheckFrequency { Startup, Hourly, Daily, Weekly, Monthly, Off }

internal sealed class UpdateCheckFrequencyConverter : JsonConverter<UpdateCheckFrequency>
{
    public override UpdateCheckFrequency Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<UpdateCheckFrequency>(reader.GetString(), true, out var value)
            && Enum.IsDefined(value)) return value;
        using var ignored = JsonDocument.ParseValue(ref reader);
        return UpdateCheckFrequency.Startup;
    }
    public override void Write(Utf8JsonWriter writer, UpdateCheckFrequency value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

internal sealed record UpdateScheduleCheckpoint(DateTimeOffset? LastAttemptUtc = null, DateTimeOffset? LastSuccessUtc = null,
    UpdateRateLimitCheckpoint? RateLimit = null);

internal sealed record UpdateScheduleEdit<T>(T Expected, T Next);

internal sealed record UpdateRateLimitRepair(UpdateRateLimitCheckpoint Read, bool CountKnown, bool DelayKnown,
    TimeSpan? ReadDelay, UpdateRateLimitCheckpoint Recovered);

internal sealed record UpdateScheduleCommit(UpdateScheduleCheckpoint Checkpoint, bool RateLimitSaved, bool AttemptBlocked = false,
    UpdateRateLimitRepair? Repair = null, Exception? WriteError = null, bool RateLimitKnown = true);

internal sealed record UpdateSchedulePatch(UpdateScheduleEdit<DateTimeOffset?>? Attempt = null,
    UpdateScheduleEdit<DateTimeOffset?>? Success = null, UpdateScheduleEdit<UpdateRateLimitCheckpoint?>? RateLimit = null)
{
    internal bool HasChanges => Attempt is not null || Success is not null || RateLimit is not null;
}

internal static class UpdateSchedulePolicy
{
    internal static UpdateScheduleCheckpoint Normalize(UpdateScheduleCheckpoint value, DateTimeOffset now)
    {
        DateTimeOffset? Bounded(DateTimeOffset? date) => date is null || date < DateTimeOffset.UnixEpoch ? null : date > now ? now : date.Value.ToUniversalTime();
        return new(Bounded(value.LastAttemptUtc), Bounded(value.LastSuccessUtc), NormalizeRateLimit(value.RateLimit));
    }

    internal static UpdateRateLimitCheckpoint? NormalizeRateLimit(UpdateRateLimitCheckpoint? rateLimit)
        => rateLimit is null || rateLimit.RetryNotBeforeUtc < DateTimeOffset.UnixEpoch ? null : new(rateLimit.RetryNotBeforeUtc.ToUniversalTime(),
            Math.Clamp(rateLimit.SecondaryLimitCount, 0, UpdateRateLimitCheckpoint.MaximumSecondaryLimitCount),
            rateLimit.SecondaryLimitCount > 0 && rateLimit.SecondaryLimitDelay >= TimeSpan.Zero ? rateLimit.SecondaryLimitDelay : null);

    internal static DateTimeOffset? NextDue(UpdateCheckFrequency frequency, UpdateScheduleCheckpoint checkpoint, DateTimeOffset now,
        TimeSpan remainingRateLimit = default)
    {
        if (frequency is UpdateCheckFrequency.Off or UpdateCheckFrequency.Startup || !Enum.IsDefined(frequency)) return null;
        var previous = Normalize(checkpoint, now).LastAttemptUtc;
        var next = now;
        // A month is a calendar month in UTC, rather than thirty days.
        try
        {
            if (previous is { } lastAttempt) next = frequency switch
            {
                UpdateCheckFrequency.Hourly => lastAttempt.AddHours(1),
                UpdateCheckFrequency.Daily => lastAttempt.AddDays(1),
                UpdateCheckFrequency.Weekly => lastAttempt.AddDays(7),
                UpdateCheckFrequency.Monthly => lastAttempt.AddMonths(1),
                _ => now
            };
            if (remainingRateLimit > TimeSpan.Zero && now + remainingRateLimit > next) next = now + remainingRateLimit;
            return next;
        }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MaxValue; }
    }
}

// The checkpoint is separate from settings: a timer must never commit an open
// settings draft or rewrite playback preferences. It contains no account data.
internal sealed class UpdateScheduleStore
{
    private const int MaximumFileBytes = 4096;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 4 };
    internal string FilePath { get; }
    internal Action? BeforeRepairForChecks { get; init; }
    internal UpdateScheduleStore(string directory) => FilePath = Path.Combine(Path.GetFullPath(directory), "update-schedule.json");
    internal UpdateScheduleCheckpoint Load(DateTimeOffset now)
    {
        return TryLoad(out var checkpoint) ? UpdateSchedulePolicy.Normalize(checkpoint, now) : new();
    }

    internal bool TryLoad(out UpdateScheduleCheckpoint checkpoint)
    {
        checkpoint = new();
        try
        {
            using var parents = UninstallPathGuard.HoldAncestors(FilePath);
            if (!parents.Complete) return true;
            byte[] bytes;
            ParsedCheckpoint parsed;
            FileSecurityPreservation.FileRevision damageRevision;
            using (var file = GuardedStoreFile.OpenForRead(FilePath))
            {
                if (file is null) return true;
                bytes = ReadCheckpointBytes(file);
                parsed = ParseCheckpoint(bytes);
                if (!parsed.Damaged) { checkpoint = parsed.Value; return true; }
                damageRevision = FileSecurityPreservation.GetPersistentRevision(file) ?? throw CheckpointUnavailable();
            }
            BeforeRepairForChecks?.Invoke();
            using var transaction = GuardedStoreFile.BeginWrite(FilePath, createParents: false);
            RepairCheckpoint(transaction, bytes, damageRevision, parsed.Document);
            checkpoint = parsed.Value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }
    internal void Save(UpdateScheduleCheckpoint checkpoint)
        => GuardedStoreFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(checkpoint));

    internal static bool IsOwnedFile(SafeFileHandle file)
    {
        if (GuardedStoreFile.ReadFromHandle(file, MaximumFileBytes) is not { } bytes) return false;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = Json.MaxDepth });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            var parsed = ParseCheckpoint(bytes);
            if (parsed.Damaged || parsed.Document.Count == 0 || parsed.Document.Any(pair => pair.Key is not
                (nameof(UpdateScheduleCheckpoint.LastAttemptUtc) or nameof(UpdateScheduleCheckpoint.LastSuccessUtc) or nameof(UpdateScheduleCheckpoint.RateLimit))))
                return false;
            return parsed.Document[nameof(UpdateScheduleCheckpoint.RateLimit)] is not JsonObject rateLimit
                || rateLimit.All(pair => pair.Key is nameof(UpdateRateLimitCheckpoint.RetryNotBeforeUtc)
                    or nameof(UpdateRateLimitCheckpoint.SecondaryLimitCount) or nameof(UpdateRateLimitCheckpoint.SecondaryLimitDelay));
        }
        catch (JsonException) { return false; }
    }

    internal UpdateScheduleCommit Apply(UpdateSchedulePatch patch, DateTimeOffset now, UpdateScheduleCheckpoint? lastObserved = null,
        bool admittingAttempt = false, UpdateRateLimitRepair? pendingRepair = null, UpdateCheckFrequency? scheduledFrequency = null,
        DateTimeOffset? admittedAttemptUtc = null)
    {
        using var transaction = GuardedStoreFile.BeginWrite(FilePath);
        var parsed = new ParsedCheckpoint(new(), new(), false);
        byte[]? originalBytes = null;
        if (transaction.Exists)
        {
            // Read the held original before asking whether its metadata permits replacement.
            originalBytes = transaction.ReadExisting(MaximumFileBytes);
            if (originalBytes is null)
            {
                using var original = transaction.OpenExisting() ?? throw CheckpointUnavailable();
                originalBytes = ReadCheckpointBytes(original);
            }
            parsed = ParseCheckpoint(originalBytes);
        }
        var current = parsed.Value;
        var fallback = UpdateSchedulePolicy.Normalize(lastObserved ?? new(), now);
        // Only unreadable fields in a guarded repair fall back to a prior observation.
        var previousAttempt = ReadBeforeUpdate(current.LastAttemptUtc, parsed.AttemptKnown, patch.Attempt, fallback.LastAttemptUtc);
        var previousSuccess = ReadBeforeUpdate(current.LastSuccessUtc, parsed.SuccessKnown, patch.Success, fallback.LastSuccessUtc);
        var partial = parsed.PartialRateHistory;
        var repairMatches = pendingRepair is not null && partial is { } observedHistory && current.RateLimit == pendingRepair.Read
            && observedHistory.CountKnown == pendingRepair.CountKnown && observedHistory.DelayKnown == pendingRepair.DelayKnown
            && observedHistory.Delay == pendingRepair.ReadDelay;
        var previousLimit = repairMatches ? pendingRepair!.Recovered
            : ReadBeforeRateUpdate(parsed, patch.RateLimit is { } observed ? observed.Expected : fallback.RateLimit);
        // A new later deadline can veto the attempt. The service already tracks the observed deadline monotonically.
        var knownLimit = pendingRepair?.Recovered ?? fallback.RateLimit;
        var lastAttempt = previousAttempt < fallback.LastAttemptUtc || previousAttempt is null ? fallback.LastAttemptUtc : previousAttempt;
        var observedTimes = UpdateSchedulePolicy.Normalize(new(lastAttempt, previousSuccess, previousLimit), now);
        if (admittingAttempt && previousLimit is { } active && active.RetryNotBeforeUtc > now
            && (knownLimit is null || active.RetryNotBeforeUtc > knownLimit.RetryNotBeforeUtc))
        {
            var repair = partial is { } history && current.RateLimit is { } raw
                ? new UpdateRateLimitRepair(raw, history.CountKnown, history.DelayKnown, history.Delay,
                    MergeRateHistory(active, knownLimit, history.Delay, active.RetryNotBeforeUtc)) : null;
            return new(observedTimes with { RateLimit = repair?.Recovered ?? active }, true, true, repair);
        }
        if (admittingAttempt && scheduledFrequency is { } frequency && UpdateSchedulePolicy.NextDue(frequency, observedTimes, now) > now)
            return new(observedTimes, true, true, pendingRepair);

        var attempt = MergeTime(previousAttempt, patch.Attempt);
        // A confirmed attempt survives a readable older copy. A guarded damage repair still respects explicit nullable times.
        if (admittedAttemptUtc is { } confirmedAttempt && (!parsed.Damaged || !parsed.AttemptKnown || previousAttempt is not null)
            && (attempt is null || attempt < confirmedAttempt)) attempt = confirmedAttempt;
        var success = MergeTime(previousSuccess, patch.Success);
        if (attempt != current.LastAttemptUtc) parsed.Document[nameof(UpdateScheduleCheckpoint.LastAttemptUtc)] = JsonSerializer.SerializeToNode(attempt);
        if (success != current.LastSuccessUtc) parsed.Document[nameof(UpdateScheduleCheckpoint.LastSuccessUtc)] = JsonSerializer.SerializeToNode(success);
        var rateLimit = MergeRateLimit(parsed, previousLimit, patch.RateLimit, now);
        if (pendingRepair is not null && !repairMatches && parsed.Damaged && patch.RateLimit is null)
            throw CheckpointUnavailable();
        if (rateLimit != current.RateLimit) PatchRateLimit(parsed.Document, rateLimit);
        var merged = new UpdateScheduleCheckpoint(attempt, success, rateLimit);
        try
        {
            if (parsed.Damaged)
            {
                FileSecurityPreservation.FileRevision revision;
                using (var original = transaction.OpenExisting() ?? throw CheckpointUnavailable())
                    revision = FileSecurityPreservation.GetPersistentRevision(original) ?? throw CheckpointUnavailable();
                RepairCheckpoint(transaction, originalBytes!, revision, parsed.Document);
            }
            else if (merged != current) transaction.Write(SerializeCheckpoint(parsed.Document));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Time edits still need their own retry. The complete rate observation survives a failed replacement.
            var read = current.RateLimit;
            var effective = read is { } readLimit && readLimit.RetryNotBeforeUtc > now
                && rateLimit is { } mergedLimit && mergedLimit.RetryNotBeforeUtc >= readLimit.RetryNotBeforeUtc ? mergedLimit : read;
            return new(current with { RateLimit = effective }, false, WriteError: error,
                RateLimitKnown: parsed.RateLimitKnown && parsed.PartialRateHistory is null);
        }
        // A different active checkpoint may only satisfy the pending limit if it covers at least the same deadline.
        var limitSaved = patch.RateLimit?.Next is not { } next || next.RetryNotBeforeUtc <= now
            || rateLimit?.RetryNotBeforeUtc >= next.RetryNotBeforeUtc;
        var repairRemaining = pendingRepair is not null && !repairMatches && patch.RateLimit is null
            && pendingRepair.Recovered.RetryNotBeforeUtc > now && !(rateLimit?.RetryNotBeforeUtc >= pendingRepair.Recovered.RetryNotBeforeUtc);
        return new(UpdateSchedulePolicy.Normalize(merged, now), limitSaved && !repairRemaining, Repair: repairRemaining ? pendingRepair : null);
    }

    private static UpdateRateLimitCheckpoint? MergeRateLimit(ParsedCheckpoint parsed, UpdateRateLimitCheckpoint? stored,
        UpdateScheduleEdit<UpdateRateLimitCheckpoint?>? edit, DateTimeOffset now)
    {
        if (edit is null) return stored;
        if (stored == edit.Expected) return edit.Next;
        if (edit.Next is not { } accepted) return stored;
        // A valid empty slot can coexist with a newly classified response from an admitted Latest.
        if (stored is null) return accepted.RetryNotBeforeUtc > now ? accepted : null;
        if (stored.RetryNotBeforeUtc <= now && accepted.RetryNotBeforeUtc <= now) return stored;
        var deadline = stored.RetryNotBeforeUtc > accepted.RetryNotBeforeUtc ? stored.RetryNotBeforeUtc : accepted.RetryNotBeforeUtc;
        return MergeRateHistory(stored, accepted, parsed.PartialRateHistory?.Delay, deadline);
    }

    private static UpdateRateLimitCheckpoint MergeRateHistory(UpdateRateLimitCheckpoint stored, UpdateRateLimitCheckpoint? observed,
        TimeSpan? partialDelay, DateTimeOffset deadline)
    {
        var history = observed?.SecondaryLimitDelay;
        var delay = partialDelay ?? stored.SecondaryLimitDelay;
        if (history > delay || delay is null) delay = history;
        var count = Math.Max(stored.SecondaryLimitCount, observed?.SecondaryLimitCount ?? 0);
        return new(deadline, count, count > 0 && delay >= TimeSpan.Zero ? delay : null);
    }

    private static T ReadBeforeUpdate<T>(T stored, bool known, UpdateScheduleEdit<T>? edit, T fallback)
        => known ? stored : edit is { } observed ? observed.Expected : fallback;

    private static UpdateRateLimitCheckpoint? ReadBeforeRateUpdate(ParsedCheckpoint parsed, UpdateRateLimitCheckpoint? observed)
    {
        if (!parsed.RateLimitKnown) return observed;
        var stored = parsed.Value.RateLimit;
        // Repair may recover damaged history only when every readable part still matches the observed decision.
        if (parsed.PartialRateHistory is { } history && stored is not null && observed is not null
            && stored.RetryNotBeforeUtc == observed.RetryNotBeforeUtc
            && (!history.CountKnown || stored.SecondaryLimitCount == observed.SecondaryLimitCount)
            && (!history.DelayKnown || history.Delay == observed.SecondaryLimitDelay)) return observed;
        return stored;
    }

    private static DateTimeOffset? MergeTime(DateTimeOffset? current, UpdateScheduleEdit<DateTimeOffset?>? edit)
        => edit is not null && (current == edit.Expected || edit.Next is not null && (current is null || current < edit.Next))
            ? edit.Next : current;

    private static void PatchRateLimit(JsonObject document, UpdateRateLimitCheckpoint? next)
    {
        const string key = nameof(UpdateScheduleCheckpoint.RateLimit);
        var rateLimit = document[key];
        if (rateLimit is not JsonObject fields)
        {
            if (next is null) return;
            document[key] = fields = new JsonObject();
        }
        const string time = nameof(UpdateRateLimitCheckpoint.RetryNotBeforeUtc);
        const string count = nameof(UpdateRateLimitCheckpoint.SecondaryLimitCount);
        const string delay = nameof(UpdateRateLimitCheckpoint.SecondaryLimitDelay);
        if (next is not null)
        {
            fields[time] = JsonValue.Create(next.RetryNotBeforeUtc);
            fields[count] = JsonValue.Create(next.SecondaryLimitCount);
            fields[delay] = JsonSerializer.SerializeToNode(next.SecondaryLimitDelay);
        }
        else
        {
            fields.Remove(time);
            fields.Remove(count);
            fields.Remove(delay);
            if (fields.Count == 0) document[key] = null;
        }
    }

    private readonly record struct PartialRateHistory(bool CountKnown, bool DelayKnown, TimeSpan? Delay);

    private sealed record ParsedCheckpoint(JsonObject Document, UpdateScheduleCheckpoint Value, bool Damaged,
        bool AttemptKnown = true, bool SuccessKnown = true, bool RateLimitKnown = true, PartialRateHistory? PartialRateHistory = null)
    {
        internal static ParsedCheckpoint Unreadable() => new(new(), new(), true, false, false, false);
    }

    private static ParsedCheckpoint ParseCheckpoint(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            if (JsonNode.Parse(stream, documentOptions: new() { MaxDepth = Json.MaxDepth }) is not JsonObject document)
                return ParsedCheckpoint.Unreadable();
            var damaged = false;
            T? ReadField<T>(JsonObject obj, string key, out bool known, bool allowNull = true) where T : struct
            {
                known = true;
                if (obj[key] is not { } node)
                {
                    if (!allowNull && obj.Remove(key)) { damaged = true; known = false; }
                    return null;
                }
                try { return node.Deserialize<T>(Json); }
                catch (JsonException) { obj.Remove(key); damaged = true; known = false; return null; }
            }
            var attempt = ReadField<DateTimeOffset>(document, nameof(UpdateScheduleCheckpoint.LastAttemptUtc), out var attemptKnown);
            var success = ReadField<DateTimeOffset>(document, nameof(UpdateScheduleCheckpoint.LastSuccessUtc), out var successKnown);
            const string rateKey = nameof(UpdateScheduleCheckpoint.RateLimit);
            UpdateRateLimitCheckpoint? rateLimit = null;
            var rateLimitKnown = true;
            PartialRateHistory? partialHistory = null;
            if (document[rateKey] is JsonObject fields)
            {
                const string untilKey = nameof(UpdateRateLimitCheckpoint.RetryNotBeforeUtc);
                const string countKey = nameof(UpdateRateLimitCheckpoint.SecondaryLimitCount);
                const string delayKey = nameof(UpdateRateLimitCheckpoint.SecondaryLimitDelay);
                var hasLimitDetails = fields.ContainsKey(countKey) || fields[delayKey] is not null;
                var until = ReadField<DateTimeOffset>(fields, untilKey, out rateLimitKnown);
                var count = ReadField<int>(fields, countKey, out var countKnown, allowNull: false) ?? 0;
                var delay = ReadField<TimeSpan>(fields, delayKey, out var delayKnown);
                if (until is { } valid)
                {
                    rateLimit = UpdateSchedulePolicy.NormalizeRateLimit(new(valid, count, delay));
                    if (!countKnown || !delayKnown) partialHistory = new(countKnown, delayKnown, delay);
                }
                else if (hasLimitDetails)
                {
                    fields.Remove(countKey);
                    fields.Remove(delayKey);
                    damaged = true;
                    rateLimitKnown = false;
                }
            }
            else if (document[rateKey] is not null) { document.Remove(rateKey); damaged = true; rateLimitKnown = false; }
            return new(document, new(attempt, success, rateLimit), damaged, attemptKnown, successKnown, rateLimitKnown, partialHistory);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return ParsedCheckpoint.Unreadable(); }
    }

    private static byte[] ReadCheckpointBytes(SafeFileHandle file)
        => RandomAccess.GetLength(file) == 0 ? [] : GuardedStoreFile.ReadFromHandle(file, MaximumFileBytes) ?? throw CheckpointUnavailable();

    private static byte[] SerializeCheckpoint(JsonObject document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        return bytes.Length <= MaximumFileBytes ? bytes : throw CheckpointUnavailable();
    }

    private void RepairCheckpoint(GuardedStoreFile.WriteTransaction transaction, byte[] expected,
        FileSecurityPreservation.FileRevision revision, JsonObject document)
    {
        var replacement = SerializeCheckpoint(document);
        SafeFileHandle? archive = null;
        var committed = false;
        try
        {
            using (var original = transaction.OpenExisting() ?? throw CheckpointUnavailable())
            {
                if (FileSecurityPreservation.GetPersistentRevision(original) != revision
                    || !ReadCheckpointBytes(original).AsSpan().SequenceEqual(expected)) throw CheckpointUnavailable();
                archive = transaction.CreateStage($"{FilePath}.corrupt-{Guid.NewGuid():N}.bak");
                RandomAccess.Write(archive, expected, 0);
                FileSecurityPreservation.PreserveBasicInformation(original, archive);
                RandomAccess.FlushToDisk(archive);
            }
            UninstallPathGuard.KeepFileOnClose(archive);
            transaction.Write(replacement);
            committed = true;
        }
        finally
        {
            if (archive is not null)
            {
                if (!committed) UninstallPathGuard.DiscardStagedFile(archive);
                archive.Dispose();
            }
        }
    }

    private static IOException CheckpointUnavailable() => new("Не удалось подтвердить контрольную точку обновлений.");
}

public partial class MainWindow
{
    private UpdateCheckFrequency _updateCheckFrequency = UpdateCheckFrequency.Startup;
    private UpdateScheduleStore? _updateScheduleStore;
    private UpdateScheduleCheckpoint _updateCheckpoint = new();
    private UpdateSchedulePatch _pendingUpdateSchedule = new();
    private UpdateRateLimitRepair? _pendingUpdateRateRepair;
    private bool _updateCheckpointNeedsVerification;
    private Func<DateTimeOffset> _updateClock = static () => DateTimeOffset.UtcNow;
    private DispatcherTimer? _updateScheduleTimer;
    private bool _updateScheduleInitialized, _updateCheckpointLoaded, _updateCheckInProgress, _startupUpdatePending;
    private TaskCompletionSource? _updateCheckCompletion;
    private TimeSpan _updateCheckExitTimeout = TimeSpan.FromSeconds(3);
    private ComboBox? _updateFrequencyCombo;
    private Action<Uri>? _updateLinkLauncher = null;
    private static readonly Uri ProjectUri = UpdateSupport.OfficialRepository;
    private static readonly Uri ReleasesUri = new("https://github.com/senduora/yamuse/releases");
    private const string UpdateScheduleSaveFailure = "Не удалось сохранить время проверки обновлений.";
    private const string UpdateScheduleReadFailure = "Не удалось прочитать расписание обновлений. Повторите проверку позже.";
    private const string UpdateCheckStillRunning = "Не удалось дождаться завершения проверки обновлений.";

    private void InitializeUpdateSchedule()
    {
        if (_updateScheduleInitialized || _closed) return;
        _updateScheduleInitialized = true;
        if (_preferenceStore is not null) _updateScheduleStore ??= new(Path.GetDirectoryName(_preferenceStore.FilePath)!);
        Closed += CloseUpdateSchedule;
    }

    private bool TryLoadUpdateCheckpoint()
    {
        if (_updateCheckpointLoaded) return true;
        var now = _updateClock();
        if (_updateScheduleStore is { } store && !store.TryLoad(out _updateCheckpoint)) return false;
        _updateCheckpointLoaded = true;
        AppUpdates.RestoreRateLimit(_updateCheckpoint.RateLimit);
        // Commit a future-clock reset once so reopening the app cannot keep
        // moving the next attempt farther into the future.
        ObserveUpdateTimes(UpdateSchedulePolicy.Normalize(_updateCheckpoint, now));
        FlushUpdateSchedule();
        return true;
    }

    private void CloseUpdateSchedule(object? sender, EventArgs e)
    {
        TryCommitUpdateSchedule();
        if (_updateScheduleTimer is not null)
        { _updateScheduleTimer.Stop(); _updateScheduleTimer.Tick -= UpdateSchedule_Tick; _updateScheduleTimer = null; }
        Closed -= CloseUpdateSchedule;
    }

    private bool TryCommitUpdateSchedule(UpdateSupport? updates = null)
    {
        if (_updateCheckpointLoaded) ObserveUpdateRateLimit((updates ?? AppUpdates).RateLimitCheckpoint);
        return FlushUpdateSchedule(updates).Committed;
    }

    private async Task<bool> SettleUpdateCheckAsync()
    {
        if (_updateCheckCompletion is not { } check) return true;
        try
        {
            await check.Task.WaitAsync(_updateCheckExitTimeout, _lifetime.Token);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private (bool Committed, bool AttemptBlocked) FlushUpdateSchedule(UpdateSupport? updates = null,
        UpdateScheduleCheckpoint? attemptBaseline = null, UpdateCheckFrequency? scheduledFrequency = null)
    {
        if (_updateScheduleStore is not { } store) return (true, false);
        var attemptOnly = attemptBaseline is not null;
        var patch = attemptOnly ? _pendingUpdateSchedule with { RateLimit = null } : _pendingUpdateSchedule;
        if (!attemptOnly && !patch.HasChanges && !_updateCheckpointNeedsVerification && _pendingUpdateRateRepair is null)
            return (true, false);
        try
        {
            var now = _updateClock();
            var pendingRepair = _pendingUpdateRateRepair;
            var baseline = attemptBaseline ?? _updateCheckpoint;
            var committed = store.Apply(patch, now, baseline, admittingAttempt: attemptOnly, pendingRepair: pendingRepair,
                scheduledFrequency: scheduledFrequency, admittedAttemptUtc: _updateCheckpointNeedsVerification ? baseline.LastAttemptUtc : null);
            var saved = committed.Checkpoint;
            var adoptHistory = attemptOnly && _pendingUpdateSchedule.RateLimit is null && pendingRepair is null;
            if (committed.WriteError is { } error)
            {
                if (committed.RateLimitKnown && (adoptHistory || saved.RateLimit?.RetryNotBeforeUtc > now))
                    ReconcileUpdateRateLimit(updates, saved.RateLimit, adoptHistory);
                _diagnostics.LastErrorType = "UpdateScheduleSave:" + error.GetType().Name;
                return (false, false);
            }
            if (committed.AttemptBlocked)
            {
                var service = updates ?? AppUpdates;
                if (saved.RateLimit?.RetryNotBeforeUtc > now) service.RestoreRateLimit(saved.RateLimit);
                _pendingUpdateRateRepair = committed.Repair;
                _updateCheckpoint = _updateCheckpoint with { LastAttemptUtc = saved.LastAttemptUtc, LastSuccessUtc = saved.LastSuccessUtc };
                if (committed.Repair is null && _pendingUpdateSchedule.RateLimit is null)
                    _updateCheckpoint = _updateCheckpoint with { RateLimit = service.RateLimitCheckpoint };
                return (false, true);
            }
            _updateCheckpointNeedsVerification = false;
            _pendingUpdateRateRepair = committed.Repair;
            _pendingUpdateSchedule = new(RateLimit: patch.RateLimit is not null && committed.RateLimitSaved ? null : _pendingUpdateSchedule.RateLimit);
            _updateCheckpoint = _updateCheckpoint with { LastAttemptUtc = saved.LastAttemptUtc, LastSuccessUtc = saved.LastSuccessUtc };
            // Scope the CAS baseline to the guarded read or a completed repair, including when there was no local rate edit.
            if (committed.RateLimitSaved && (adoptHistory || saved.RateLimit is { } retained
                && (retained.RetryNotBeforeUtc > now || retained == pendingRepair?.Recovered)))
                ReconcileUpdateRateLimit(updates, saved.RateLimit, adoptHistory);
            return (committed.RateLimitSaved, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _diagnostics.LastErrorType = "UpdateScheduleSave:" + ex.GetType().Name;
            return (false, false);
        }
    }

    private void ReconcileUpdateRateLimit(UpdateSupport? updates, UpdateRateLimitCheckpoint? checkpoint, bool adoptHistory)
    {
        var service = updates ?? AppUpdates;
        if (adoptHistory) service.ObserveRateLimitAtAdmission(checkpoint);
        else service.RestoreRateLimit(checkpoint);
        if (_pendingUpdateSchedule.RateLimit is null && _pendingUpdateRateRepair is null)
            _updateCheckpoint = _updateCheckpoint with { RateLimit = service.RateLimitCheckpoint };
    }

    private void ObserveUpdateTimes(UpdateScheduleCheckpoint next)
    {
        _pendingUpdateSchedule = _pendingUpdateSchedule with
        {
            Attempt = ObserveScheduleField(_pendingUpdateSchedule.Attempt, _updateCheckpoint.LastAttemptUtc, next.LastAttemptUtc),
            Success = ObserveScheduleField(_pendingUpdateSchedule.Success, _updateCheckpoint.LastSuccessUtc, next.LastSuccessUtc)
        };
        _updateCheckpoint = _updateCheckpoint with { LastAttemptUtc = next.LastAttemptUtc, LastSuccessUtc = next.LastSuccessUtc };
    }

    private void ObserveUpdateRateLimit(UpdateRateLimitCheckpoint? rateLimit)
    {
        var previous = _pendingUpdateRateRepair?.Recovered ?? _updateCheckpoint.RateLimit;
        if (_pendingUpdateRateRepair is not null && rateLimit == previous) return;
        _pendingUpdateSchedule = _pendingUpdateSchedule with
        { RateLimit = ObserveScheduleField(_pendingUpdateSchedule.RateLimit, previous, rateLimit) };
        _updateCheckpoint = _updateCheckpoint with { RateLimit = rateLimit };
    }

    private static UpdateScheduleEdit<T>? ObserveScheduleField<T>(UpdateScheduleEdit<T>? pending, T previous, T next)
    {
        var comparer = EqualityComparer<T>.Default;
        if (comparer.Equals(previous, next)) return pending;
        var expected = pending is null ? previous : pending.Expected;
        return comparer.Equals(expected, next) ? null : new(expected, next);
    }

    private void ArmUpdateSchedule()
    {
        _updateScheduleTimer?.Stop();
        if (_updateCheckFrequency != UpdateCheckFrequency.Startup) _startupUpdatePending = false;
        if (!_updateScheduleInitialized || !UpdateFrequencyKnown || _closed || _lifetime.IsCancellationRequested || _closeCommitInProgress
            || _startupUpdateRestartRequested || !AppUpdates.State.FeedConfigured) return;
        var now = _updateClock();
        var normalized = UpdateSchedulePolicy.Normalize(_updateCheckpoint, now);
        if (normalized != _updateCheckpoint) { ObserveUpdateTimes(normalized); FlushUpdateSchedule(); }
        var next = NextUpdateCheck(now);
        if (next is null) return;
        if (_updateScheduleTimer is null)
        {
            _updateScheduleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _updateScheduleTimer.Tick += UpdateSchedule_Tick;
        }
        // Reassess the wall clock at least hourly after clock changes or resume;
        // no per-frame timer, busy loop, or callback survives the window.
        var delay = next.Value - now;
        _updateScheduleTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds,
            _updateCheckInProgress || _appUpdateActionInProgress ? 30000 : 1000, 3600000));
        _updateScheduleTimer.Start();
    }

    private async void UpdateSchedule_Tick(object? sender, EventArgs e)
    {
        _updateScheduleTimer?.Stop();
        await RunScheduledUpdateCheckAsync();
    }

    private async Task RunScheduledUpdateCheckAsync()
    {
        if (!UpdateFrequencyKnown || _closed || _lifetime.IsCancellationRequested || _closeCommitInProgress || _startupUpdateRestartRequested) return;
        InitializeUpdateSchedule();
        var now = _updateClock();
        var next = TryLoadUpdateCheckpoint() ? NextUpdateCheck(now) : null;
        if (next is not null && next <= now && !_appUpdateActionInProgress && !_updateCheckInProgress)
        {
            if (_updateCheckFrequency == UpdateCheckFrequency.Startup) await CheckStartupUpdateNowAsync(AppUpdates);
            else await CheckAppUpdateCoreAsync(AppUpdates, manual: false, scheduledFrequency: _updateCheckFrequency);
        }
        else TryCommitUpdateSchedule();
        ArmUpdateSchedule();
    }

    private DateTimeOffset? NextUpdateCheck(DateTimeOffset now)
    {
        if (_updateCheckFrequency == UpdateCheckFrequency.Off
            || _updateCheckFrequency == UpdateCheckFrequency.Startup && !_startupUpdatePending) return null;
        if (!_updateCheckpointLoaded) return now.AddSeconds(30);
        var remaining = AppUpdates.RemainingRateLimit;
        if (_updateCheckFrequency != UpdateCheckFrequency.Startup)
            return UpdateSchedulePolicy.NextDue(_updateCheckFrequency, _updateCheckpoint, now, remaining);
        if (remaining <= TimeSpan.Zero) return now;
        return remaining <= DateTimeOffset.MaxValue - now ? now + remaining : DateTimeOffset.MaxValue;
    }

    private Task CheckAppUpdateNowAsync(UpdateSupport updates) => CheckAppUpdateCoreAsync(updates, manual: false);

    private Task CheckManualUpdateNowAsync(UpdateSupport updates) => CheckAppUpdateCoreAsync(updates, manual: true);

    private async Task CheckAppUpdateCoreAsync(UpdateSupport updates, bool manual, UpdateCheckFrequency? scheduledFrequency = null)
    {
        if (_closed || _closeCommitInProgress || _appUpdatePreparingRestart || _updateCheckInProgress || !updates.State.FeedConfigured
            || _lifetime.IsCancellationRequested || _startupUpdateRestartRequested) return;
        InitializeUpdateSchedule();
        if (!TryLoadUpdateCheckpoint())
        {
            if (manual)
            {
                _diagnostics.LastErrorType = "UpdateScheduleRead:Unavailable";
                _appUpdateFailure = UpdateScheduleReadFailure;
                RefreshAppUpdateUi();
            }
            ArmUpdateSchedule();
            return;
        }
        // Resolve previously pending rate decisions before reserving a new request time.
        if (_pendingUpdateSchedule.RateLimit is not null || _pendingUpdateRateRepair is not null) FlushUpdateSchedule(updates);
        _updateCheckInProgress = true;
        var completion = _updateCheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = updates.State;
        if (before.Status != AppUpdateStatus.Downloaded || _appUpdateFailure == UpdateScheduleReadFailure) _appUpdateFailure = null;
        try
        {
            var admitted = false;
            var pending = updates.CheckAsync(_lifetime.Token, async () =>
                admitted = await RecordUpdateAttemptAsync(updates, scheduledFrequency).ConfigureAwait(false));
            RefreshAppUpdateUi();
            var result = await pending;
            _updateCheckpointNeedsVerification |= admitted;
            ObserveUpdateRateLimit(updates.RateLimitCheckpoint);
            if (result.CheckSucceeded)
            {
                var now = _updateClock();
                ObserveUpdateTimes(_updateCheckpoint with { LastSuccessUtc = now });
            }
            FlushUpdateSchedule(updates);
            if (_closed || _lifetime.IsCancellationRequested) return;
            if (result.Status != before.Status || !string.Equals(result.AvailableVersion, before.AvailableVersion, StringComparison.Ordinal))
                _appUpdateFailure = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name; }
        finally
        {
            _updateCheckInProgress = false;
            _updateCheckCompletion = null;
            completion.TrySetResult();
            if (!_closed) { RefreshAppUpdateUi(); ArmUpdateSchedule(); }
        }
    }

    private Task<bool> RecordUpdateAttemptAsync(UpdateSupport updates, UpdateCheckFrequency? scheduledFrequency)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(Record).Task;
        return Task.FromResult(Record());

        bool Record()
        {
            if (_closed || _lifetime.IsCancellationRequested || updates.RemainingRateLimit > TimeSpan.Zero) return false;
            var priorPatch = _pendingUpdateSchedule;
            var attemptBaseline = _updateCheckpoint;
            var now = _updateClock();
            ObserveUpdateTimes(UpdateSchedulePolicy.Normalize(_updateCheckpoint, now) with { LastAttemptUtc = now });
            if (FlushUpdateSchedule(updates, attemptBaseline, scheduledFrequency).AttemptBlocked)
            {
                _pendingUpdateSchedule = priorPatch;
                return false;
            }
            _startupUpdatePending = false;
            return true;
        }
    }

    private async void FooterUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _closeCommitInProgress || _appUpdateActionInProgress || _updateCheckInProgress || _startupUpdateRestartRequested) return;
        if (AppUpdates.State.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded)
        {
            if (!AppUpdates.State.IsInstalled) OpenUpdateLink(ReleasesUri);
            else { if (!_settingsOpen) Settings_Click(SettingsButton, e); SettingsUpdates.BringIntoView(); }
            return;
        }
        await CheckManualUpdateNowAsync(AppUpdates);
    }

    private void Github_Click(object sender, RoutedEventArgs e) => OpenUpdateLink(ProjectUri);
    private void Changelog_Click(object sender, RoutedEventArgs e) => OpenUpdateLink(ReleasesUri);
    private void OpenUpdateLink(Uri uri)
    {
        if (_closed || uri != ProjectUri && uri != ReleasesUri) return;
        try
        {
            if (_updateLinkLauncher is not null) _updateLinkLauncher(uri);
            else Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { SetStatus("Не удалось открыть страницу в браузере.", true); }
    }
}
