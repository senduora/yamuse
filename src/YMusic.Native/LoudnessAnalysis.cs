using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http;

namespace YMusic.Native;

internal sealed record LoudnessResult(double IntegratedLufs, double Peak);

// An attenuation aid, not a certified meter: BS.1770 K-weighting and two-stage
// gating for mono/stereo. Peak is a decoded sample peak, not an oversampled true peak.
internal static class LoudnessAnalysis
{
    private const uint FirstAudio = 0xfffffffd, AllStreams = 0xfffffffe;
    private const int Rate = 48000;
    private const long MaximumFrames = 30L * 60 * Rate;
    private const long MaximumHttpBytes = 512L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient LoopbackClient = new(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid Alignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid BytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    internal static async Task<LoudnessResult?> AnalyzeAsync(string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TrySource(source, out var trusted)) return null;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (trusted.StartsWith("http://", StringComparison.Ordinal)) return await AnalyzeLoopbackAsync(trusted, token).ConfigureAwait(false);
            return await Task.Run(() => AnalyzeCore(trusted, token), token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static async Task<LoudnessResult?> AnalyzeLoopbackAsync(string source, CancellationToken token)
    {
        // A synchronous MF network resolver can block inside URL creation/read
        // despite Flush. Keep network cancellation and redirect policy in HttpClient;
        // the native decoder sees only an owned, completed, transient local file.
        string? temporary = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var response = await LoopbackClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > MaximumHttpBytes) return null;
            temporary = Path.Combine(Path.GetTempPath(), "ym-loudness-" + Guid.NewGuid().ToString("N") + ".audio");
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65536]; long total = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > MaximumHttpBytes) return null;
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => AnalyzeCore(temporary, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException) { token.ThrowIfCancellationRequested(); return null; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static double Gain(LoudnessResult? result)
    {
        if (result is null || !double.IsFinite(result.IntegratedLufs) || !double.IsFinite(result.Peak) || result.Peak <= 0) return 1;
        var loudnessGain = Math.Pow(10, (-18 - result.IntegratedLufs) / 20);
        return Math.Clamp(Math.Min(loudnessGain, 1 / result.Peak), 0, 1);
    }

    private static bool TrySource(string? source, out string trusted)
    {
        trusted = "";
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                if (uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" || uri.Port <= 0
                    || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
                trusted = uri.AbsoluteUri; return true;
            }
            var local = uri is { IsFile: true } ? uri.LocalPath : source;
            if (!Path.IsPathFullyQualified(local) || local.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            var file = new FileInfo(Path.GetFullPath(local));
            if (!file.Exists || file.Length == 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            for (var part = file.Directory; part is not null; part = part.Parent)
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            trusted = file.FullName; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static LoudnessResult? AnalyzeCore(string source, CancellationToken token)
    {
        IntPtr reader = IntPtr.Zero, requested = IntPtr.Zero, actual = IntPtr.Zero;
        var comStarted = false; var mediaStarted = false;
        var cancellationGate = new object();
        var cancellationReader = IntPtr.Zero;
        Task? flush = null;
        CancellationTokenRegistration cancellation = default;
        try
        {
            token.ThrowIfCancellationRequested();
            Check(CoInitializeEx(IntPtr.Zero, 0)); comStarted = true;
            Check(MFStartup(0x00020070, 0)); mediaStarted = true;
            Check(MFCreateSourceReaderFromURL(source, IntPtr.Zero, out reader));
            lock (cancellationGate) cancellationReader = reader;
            cancellation = token.UnsafeRegister(_ =>
            {
                lock (cancellationGate)
                {
                    if (cancellationReader == IntPtr.Zero) return;
                    var retained = cancellationReader;
                    Marshal.AddRef(retained);
                    // Never run a synchronous COM Flush on the UI thread that
                    // cancelled playback. Its extra reference is released on MTA.
                    flush = Task.Run(() =>
                    {
                        var initialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
                        try { if (initialized) _ = Method<FlushReader>(retained, 10)(retained, FirstAudio); }
                        finally { Marshal.Release(retained); if (initialized) CoUninitialize(); }
                    });
                }
            }, null);
            token.ThrowIfCancellationRequested();
            var select = Method<SetStreamSelection>(reader, 4);
            Check(select(reader, AllStreams, 0)); Check(select(reader, FirstAudio, 1));
            Check(Method<GetMediaType>(reader, 6)(reader, FirstAudio, out actual));
            var channels = GetUInt(actual, Channels);
            if (channels is < 1 or > 2) return null;
            Release(ref actual);
            Check(MFCreateMediaType(out requested));
            SetGuid(requested, Major, Audio); SetGuid(requested, Subtype, Float);
            SetUInt(requested, Channels, channels); SetUInt(requested, SampleRate, Rate);
            SetUInt(requested, Bits, 32); SetUInt(requested, Alignment, channels * 4);
            SetUInt(requested, BytesPerSecond, Rate * channels * 4);
            // Windows 8+ Source Reader inserts the audio decoder/resampler.
            // https://learn.microsoft.com/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsourcereader-setcurrentmediatype
            Check(Method<SetMediaType>(reader, 7)(reader, FirstAudio, IntPtr.Zero, requested));
            Check(Method<GetMediaType>(reader, 6)(reader, FirstAudio, out actual));
            Check(Method<GetGuid>(actual, 10)(actual, Subtype, out var subtype));
            if (subtype != Float || GetUInt(actual, Channels) != channels || GetUInt(actual, SampleRate) != Rate
                || GetUInt(actual, Bits) != 32 || GetUInt(actual, Alignment) != channels * 4) return null;
            var meter = new Meter((int)channels);
            var read = Method<ReadSample>(reader, 9);
            // Bounded working storage: 64 KiB copy buffer, 400 ms energy ring,
            // and at most 17,997 scalar block energies for a 30-minute track.
            var scratch = new float[16384];
            var emptyReads = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                IntPtr sample = IntPtr.Zero;
                try
                {
                    Check(read(reader, FirstAudio, 0, out _, out var flags, out _, out sample));
                    token.ThrowIfCancellationRequested();
                    if ((flags & (0x1 | 0x10 | 0x20)) != 0) return null; // Error or format change.
                    if (sample != IntPtr.Zero)
                    {
                        emptyReads = 0;
                        if (!ReadBuffers(sample, meter, scratch, token)) return null;
                    }
                    else if (++emptyReads > 1000) return null;
                    if ((flags & 0x2) != 0) return meter.Result();
                }
                finally { Release(ref sample); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException)
        { token.ThrowIfCancellationRequested(); return null; }
        finally
        {
            lock (cancellationGate) cancellationReader = IntPtr.Zero;
            cancellation.Dispose();
            // Keep the global analysis gate and MF platform alive until the
            // cancellation flush has stopped using this reader.
            flush?.GetAwaiter().GetResult();
            Release(ref actual); Release(ref requested); Release(ref reader);
            if (mediaStarted) _ = MFShutdown();
            if (comStarted) CoUninitialize();
        }
    }

    private static bool ReadBuffers(IntPtr sample, Meter meter, float[] scratch, CancellationToken token)
    {
        Check(Method<GetCount>(sample, 39)(sample, out var count));
        if (count is 0 or > 64) return false;
        var get = Method<GetBuffer>(sample, 40);
        long sampleBytes = 0;
        for (uint index = 0; index < count; index++)
        {
            IntPtr buffer = IntPtr.Zero; var locked = false;
            try
            {
                Check(get(sample, index, out buffer));
                Check(Method<LockBuffer>(buffer, 3)(buffer, out var memory, out var maximum, out var length)); locked = true;
                sampleBytes += length;
                if (length > maximum || length % 4 != 0 || sampleBytes > 32 * 1024 * 1024) return false;
                for (var offset = 0; offset < length;)
                {
                    token.ThrowIfCancellationRequested();
                    var floats = (int)Math.Min(scratch.Length, (length - offset) / 4);
                    Marshal.Copy(IntPtr.Add(memory, offset), scratch, 0, floats);
                    if (!meter.Add(scratch, floats)) return false;
                    offset += floats * 4;
                }
            }
            finally
            {
                if (locked) _ = Method<UnlockBuffer>(buffer, 4)(buffer);
                Release(ref buffer);
            }
        }
        return meter.AtFrameBoundary;
    }

    private sealed class Meter(int channels)
    {
        private const int Block = Rate * 4 / 10, Step = Rate / 10;
        private readonly double[] _ring = new double[Block];
        private readonly List<double> _blocks = new(18000);
        private readonly Filter[] _shelf = Enumerable.Range(0, channels).Select(_ => new Filter(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, .73248077421585)).ToArray();
        private readonly Filter[] _highpass = Enumerable.Range(0, channels).Select(_ => new Filter(1, -2, 1, -1.99004745483398, .99007225036621)).ToArray();
        private long _frames;
        private int _channel;
        private double _frameEnergy, _sum, _peak;
        public bool AtFrameBoundary => _channel == 0;

        public bool Add(float[] samples, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var value = (double)samples[i];
                if (!double.IsFinite(value) || Math.Abs(value) > 32 || _frames >= MaximumFrames) return false;
                _peak = Math.Max(_peak, Math.Abs(value));
                var weighted = _highpass[_channel].Process(_shelf[_channel].Process(value));
                _frameEnergy += weighted * weighted;
                if (++_channel != channels) continue;
                _channel = 0;
                var index = (int)(_frames % Block);
                _sum += _frameEnergy - _ring[index]; _ring[index] = _frameEnergy; _frameEnergy = 0;
                _frames++;
                if (_frames >= Block && (_frames - Block) % Step == 0) _blocks.Add(Math.Max(0, _sum / Block));
            }
            return true;
        }

        public LoudnessResult? Result()
        {
            if (!AtFrameBoundary || _blocks.Count == 0 || _peak == 0) return null;
            // ITU-R BS.1770 Annex 1: 400 ms blocks, 75% overlap, absolute -70
            // LUFS gate followed by a gate 10 LU below the absolute-gated mean.
            // https://www.itu.int/rec/R-REC-BS.1770/en
            var absolute = Math.Pow(10, (-70 + .691) / 10);
            double sum = 0; var count = 0;
            foreach (var block in _blocks) if (block > absolute) { sum += block; count++; }
            if (count == 0) return null;
            var relative = sum / count / 10;
            sum = 0; count = 0;
            foreach (var block in _blocks) if (block > absolute && block > relative) { sum += block; count++; }
            if (count == 0) return null;
            var lufs = -.691 + 10 * Math.Log10(sum / count);
            return double.IsFinite(lufs) ? new(lufs, _peak) : null;
        }
    }

    private sealed class Filter(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;
        public double Process(double value)
        {
            var output = b0 * value + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1; _x1 = value; _y2 = _y1; _y1 = output;
            return output;
        }
    }

    // Vtable slots and ABI come from Microsoft Windows SDK mfreadwrite.h and
    // mfobjects.h (IMFAttributes prefix, IMFSample, IMFMediaBuffer). No RCWs escape
    // this MTA thread; every returned interface owns exactly one reference.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private static void Release(ref IntPtr instance) { if (instance == IntPtr.Zero) return; _ = Marshal.Release(instance); instance = IntPtr.Zero; }
    private static uint GetUInt(IntPtr attributes, Guid key) { Check(Method<GetUInt32>(attributes, 7)(attributes, key, out var value)); return value; }
    private static void SetUInt(IntPtr attributes, Guid key, uint value) => Check(Method<SetUInt32>(attributes, 21)(attributes, key, value));
    private static void SetGuid(IntPtr attributes, Guid key, Guid value) => Check(Method<SetGuidValue>(attributes, 24)(attributes, key, value));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetStreamSelection(IntPtr self, uint stream, int selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetMediaType(IntPtr self, uint stream, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetMediaType(IntPtr self, uint stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSample(IntPtr self, uint stream, uint control, out uint actual, out uint flags, out long timestamp, out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FlushReader(IntPtr self, uint stream);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt32(IntPtr self, [In] in Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt32(IntPtr self, [In] in Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuid(IntPtr self, [In] in Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuidValue(IntPtr self, [In] in Guid key, [In] in Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCount(IntPtr self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBuffer(IntPtr self, uint index, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBuffer(IntPtr self, out IntPtr memory, out uint maximum, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UnlockBuffer(IntPtr self);

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string source, IntPtr attributes, out IntPtr reader);
}
