using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace YMusic.Native;

internal sealed record AudioOutputDevice(string Id, string Name);

// The routing seam lets fixtures exercise every slot without selecting a physical device.
internal interface IAudioOutputRoute
{
    string Id { get; }
    void Apply(Windows.Media.Playback.MediaPlayer player);
}

internal interface IAudioOutputDevices : IDisposable
{
    event Action? Changed;
    Task<IReadOnlyList<AudioOutputDevice>> ListAsync();
    Task<IAudioOutputRoute> ResolveAsync(string id);
    void Watch();
}

internal sealed class AudioOutputDevices : IAudioOutputDevices
{
    internal const string SystemDefaultLabel = "Как в Windows";
    private DeviceWatcher? _watcher;
    public event Action? Changed;

    public async Task<IReadOnlyList<AudioOutputDevice>> ListAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        return devices.Where(d => d.IsEnabled).Select(d => new AudioOutputDevice(d.Id, d.Name))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<IAudioOutputRoute> ResolveAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return SystemAudioOutputRoute.Instance;
        // Resolve only an enabled render endpoint from the official selector. A stale
        // preference must not select an input endpoint or silently start on speakers.
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        var device = devices.FirstOrDefault(d => d.IsEnabled && d.Id == id)
            ?? throw new MusicException("Выбранное аудиоустройство недоступно. Выберите другое устройство.");
        return new DeviceRoute(device);
    }

    public void Watch()
    {
        if (_watcher is { } current)
        {
            if (current.Status is not (DeviceWatcherStatus.Aborted or DeviceWatcherStatus.Stopped)) return;
            TryDisposeWatcher();
        }
        var watcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector());
        _watcher = watcher;
        try
        {
            watcher.Added += OnAdded; watcher.Removed += OnRemoved; watcher.Updated += OnUpdated;
            watcher.Start();
        }
        catch
        {
            TryDisposeWatcher();
            throw;
        }
    }
    private void OnAdded(DeviceWatcher sender, DeviceInformation args) => Changed?.Invoke();
    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => Changed?.Invoke();
    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args) => Changed?.Invoke();
    private void TryDisposeWatcher()
    {
        try { Dispose(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }
    public void Dispose()
    {
        if (_watcher is not { } watcher) return;
        _watcher = null;
        try
        {
            watcher.Added -= OnAdded; watcher.Removed -= OnRemoved; watcher.Updated -= OnUpdated;
        }
        finally
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
    }
    private sealed class DeviceRoute(DeviceInformation device) : IAudioOutputRoute
    {
        public string Id => device.Id;
        public void Apply(Windows.Media.Playback.MediaPlayer player) => player.AudioDevice = device;
    }
}

internal sealed class SystemAudioOutputRoute : IAudioOutputRoute
{
    internal static readonly SystemAudioOutputRoute Instance = new();
    public string Id => "";
    public void Apply(Windows.Media.Playback.MediaPlayer player) => player.AudioDevice = null;
}
