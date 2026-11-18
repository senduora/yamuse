using System.IO;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed record PlayerPreferences
{
    public const int DefaultConcurrentDownloads = 10;
    public AudioQuality Quality { get; init; } = AudioQuality.Excellent;
    public double Volume { get; init; } = 0.5;
    public double PlaybackSpeed { get; init; } = 1;
    public bool MinimizeToTray { get; init; }
    public bool AutoDownloadFavorites { get; init; } // Opt in; existing settings default to false.
    public int ConcurrentDownloads { get; init; } = DefaultConcurrentDownloads;
    public double CrossfadeSeconds { get; init; } = 3;
    public bool NormalizeVolume { get; init; }
    public string OutputDeviceId { get; init; } = "";
    public bool AccentFromCover { get; init; } = true;
    public string StaticAccentColor { get; init; } = "#E7BB59";
    [JsonConverter(typeof(ThemeModeConverter))]
    public string Theme { get; init; } = "System";
    public UpdateCheckFrequency UpdateCheckFrequency { get; init; } = UpdateCheckFrequency.Startup;
}

// This file contains playback preferences only; authentication remains in SessionStore.
internal sealed class PreferencesStore
{
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AudioQuality>(allowIntegerValues: false), new UpdateCheckFrequencyConverter() }
    };
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    internal string FilePath { get; }
    public bool LastLoadRecovered { get; private set; }
    public string? CorruptBackupPath { get; private set; }

    public PreferencesStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        FilePath = Path.Combine(Path.GetFullPath(directory), "settings.json");
        _gate = Gates.GetOrAdd(FilePath, static _ => new object());
    }

    public PlayerPreferences Load()
    {
        lock (_gate)
        {
            LastLoadRecovered = false;
            using var parents = HoldSettingsDirectory();
            if (!parents.Complete) return new();
            using var handle = TryOpenSettingsFile();
            if (handle is null) return new();
            using var stream = new FileStream(handle, FileAccess.Read);
            if (TryReadSettings(stream, out var preferences)) return Normalize(preferences!);

            // Save archives the original before publishing new settings.
            LastLoadRecovered = true;
            return new();
        }
    }

    public void Save(PlayerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            try { WriteSettings(preferences); }
            catch (Win32Exception error) { throw new IOException("Не удалось сохранить настройки.", error); }
        }
    }

    private void WriteSettings(PlayerPreferences preferences)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Normalize(preferences), JsonOptions);
        var directory = Path.GetDirectoryName(FilePath)!;
        var id = Guid.NewGuid().ToString("N");
        using var storage = GuardedStoreFile.BeginWrite(FilePath);
        SafeFileHandle? staged = null;
        SafeFileHandle? backup = null;
        var published = false;
        try
        {
            string? backupPath = null;
            using (var existing = storage.OpenExisting())
            {
                if (existing is not null)
                {
                    using var original = new FileStream(existing, FileAccess.Read);
                    if (original.Length > MaximumFileBytes)
                    {
                        backupPath = ArchiveOversizedOriginal(existing, directory);
                    }
                    else if (!TryReadSettings(original, out _))
                    {
                        backup = storage.CreateStage(Path.Combine(directory, $".settings-{id}-backup.tmp"));
                        WriteBackup(original, backup);
                        backupPath = Path.Combine(directory, $"settings.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{id}.json");
                        ManagedCacheCleanup.RenameByHandle(backup, backupPath);
                    }
                }
            }

            // Creating the oversized archive can update the original's metadata.
            // Stage records the original for commit after that operation.
            staged = storage.Stage(bytes);
            storage.Commit(staged);
            published = true;
            if (backupPath is not null) CorruptBackupPath = backupPath;
        }
        finally
        {
            if (!published)
            {
                if (staged is not null) UninstallPathGuard.DiscardStagedFile(staged);
                if (backup is not null) UninstallPathGuard.DiscardStagedFile(backup);
            }
            staged?.Dispose();
            backup?.Dispose();
        }
    }

    private static void WriteBackup(FileStream original, SafeFileHandle backup)
    {
        original.Position = 0;
        var buffer = new byte[64 * 1024];
        long offset = 0;
        int read;
        while ((read = original.Read(buffer)) > 0)
        {
            RandomAccess.Write(backup, buffer.AsSpan(0, read), offset);
            offset += read;
        }
        FileSecurityPreservation.PreserveBasicInformation(original.SafeFileHandle, backup);
        File.SetLastWriteTimeUtc(backup, File.GetLastWriteTimeUtc(original.SafeFileHandle));
        RandomAccess.FlushToDisk(backup);
    }

    private string ArchiveOversizedOriginal(SafeFileHandle original, string directory)
    {
        try
        {
            var identity = GetFileIdentity(original);
            // Reuse the complete archive if an earlier publication was interrupted.
            var name = $"settings.corrupt-large-{identity.Volume:x16}-{identity.High:x16}{identity.Low:x16}.json";
            var archivePath = Path.Combine(directory, name);
            // Link the checked source identity directly within the pinned directory.
            using var parent = UninstallPathGuard.HoldDirectory(directory);
            CreateArchiveLink(original, parent, name);
            using var archive = UninstallPathGuard.OpenOwnedFile(archivePath, read: false, deleteAccess: false);
            if (!GetFileIdentity(archive).Equals(identity)) throw new IOException("Резервная копия настроек занята другим файлом.");
            return archivePath;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            throw new OversizedSettingsArchiveException(error);
        }
    }

    private static void CreateArchiveLink(SafeFileHandle original, SafeFileHandle parent, string name)
    {
        const int fileLinkInformation = 11;
        var filename = Encoding.Unicode.GetBytes(name + '\0');
        var nameOffset = Marshal.OffsetOf<LinkInformation>(nameof(LinkInformation.Name)).ToInt32();
        var size = nameOffset + filename.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // ReplaceIfExists stays zero. A retry verifies the existing link's ID.
            for (var i = 0; i < nameOffset; ++i) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<LinkInformation>(nameof(LinkInformation.RootDirectory)).ToInt32(),
                parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<LinkInformation>(nameof(LinkInformation.NameLength)).ToInt32(),
                filename.Length - sizeof(char));
            Marshal.Copy(filename, 0, buffer + nameOffset, filename.Length);
            var status = NtSetInformationFile(original, out _, buffer, (uint)size, fileLinkInformation);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                var error = (int)RtlNtStatusToDosError(status);
                if (error is not (80 or 183)) throw new Win32Exception(error);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static FileIdentity GetFileIdentity(SafeFileHandle handle)
    {
        const int fileIdInfo = 18;
        if (!GetFileInformationByHandleEx(handle, fileIdInfo, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return identity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal ulong Volume;
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LinkInformation
    {
        internal byte ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint NameLength;
        internal char Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr information, uint size, int infoClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity identity, uint size);

    public static PlayerPreferences Normalize(PlayerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return preferences with
        {
            Quality = Enum.IsDefined(preferences.Quality) ? preferences.Quality : AudioQuality.Excellent,
            UpdateCheckFrequency = Enum.IsDefined(preferences.UpdateCheckFrequency) ? preferences.UpdateCheckFrequency : UpdateCheckFrequency.Startup,
            Volume = double.IsFinite(preferences.Volume) ? Math.Clamp(preferences.Volume, 0, 1) : 0.5,
            PlaybackSpeed = double.IsFinite(preferences.PlaybackSpeed) ? Math.Clamp(preferences.PlaybackSpeed, 0.5, 2) : 1,
            ConcurrentDownloads = Math.Clamp(preferences.ConcurrentDownloads, 1, 10),
            CrossfadeSeconds = double.IsFinite(preferences.CrossfadeSeconds) ? Math.Clamp(preferences.CrossfadeSeconds, 0, 10) : 3,
            OutputDeviceId = NormalizeOutputDeviceId(preferences.OutputDeviceId),
            StaticAccentColor = IsAccentColor(preferences.StaticAccentColor) ? preferences.StaticAccentColor.ToUpperInvariant() : "#E7BB59",
            Theme = NormalizeTheme(preferences.Theme)
        };
    }

    internal static bool IsAccentColor(string? value) => value is { Length: 7 } && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    internal static string NormalizeOutputDeviceId(string? value) => value is { Length: > 0 and <= 2048 }
        && !value.Any(char.IsControl) ? value : "";

    internal static string NormalizeTheme(string? value) => string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
        : string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "System";

    private UninstallPathGuard.AncestorLease HoldSettingsDirectory()
    {
        try { return UninstallPathGuard.HoldAncestors(FilePath); }
        catch (Win32Exception error) { throw new IOException("Не удалось открыть папку настроек.", error); }
    }

    private SafeFileHandle? TryOpenSettingsFile()
    {
        try { return UninstallPathGuard.OpenOwnedFile(FilePath, read: true, deleteAccess: false); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
        catch (Win32Exception error) { throw new IOException("Не удалось открыть файл настроек.", error); }
    }

    private static bool TryReadSettings(FileStream stream, out PlayerPreferences? preferences)
    {
        preferences = null;
        if (stream.Length > MaximumFileBytes) return false;
        try
        {
            preferences = JsonSerializer.Deserialize<PlayerPreferences>(stream, JsonOptions);
            return preferences is not null;
        }
        catch (JsonException) { return false; }
    }
}

internal sealed class OversizedSettingsArchiveException(Exception inner)
    : IOException("Не удалось архивировать большой файл настроек. Переименуйте settings.json в папке данных Yamuse и перезапустите приложение.", inner);

internal sealed class ThemeModeConverter : JsonConverter<string>
{
    public override bool HandleNull => true;
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return PreferencesStore.NormalizeTheme(reader.GetString());
        reader.Skip();
        return "System";
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(PreferencesStore.NormalizeTheme(value));
}
