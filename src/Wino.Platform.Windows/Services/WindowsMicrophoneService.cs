#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;

namespace Wino.Platform.Windows.Services;

/// <summary>
/// Lists the audio capture endpoints and reads a microphone's level through an <see cref="AudioGraph"/>.
/// Access needs the microphone device capability; when Windows privacy settings deny it, the graph reports
/// <see cref="AudioDeviceNodeCreationStatus.AccessDenied"/> and the caller gets <see cref="MicrophoneAccess.Denied"/>.
/// </summary>
public sealed class WindowsMicrophoneService : IMicrophoneService, IDisposable
{
    private readonly Lock _watcherLock = new();
    private EventHandler? _devicesChanged;
    private DeviceWatcher? _watcher;
    private bool _isEnumerationCompleted;
    private bool _isDisposed;

    public event EventHandler? DevicesChanged
    {
        add
        {
            lock (_watcherLock)
            {
                _devicesChanged += value;
                StartWatching();
            }
        }
        remove
        {
            lock (_watcherLock)
            {
                _devicesChanged -= value;
                if (_devicesChanged is null)
                    StopWatching();
            }
        }
    }

    public async Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<MicrophoneDevice>();

        try
        {
            var defaultId = GetDefaultCaptureId();
            var infos = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector()).AsTask(cancellationToken).ConfigureAwait(false);

            foreach (var info in infos)
            {
                if (!info.IsEnabled || string.IsNullOrEmpty(info.Id))
                    continue;

                devices.Add(new MicrophoneDevice(info.Id, info.Name ?? string.Empty, string.Equals(info.Id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not list the microphones.");
        }

        return devices;
    }

    public MicrophoneAccess GetAccessStatus()
    {
        try
        {
            return AppCapability.Create("microphone").CheckAccess() switch
            {
                AppCapabilityAccessStatus.DeniedByUser or AppCapabilityAccessStatus.DeniedBySystem => MicrophoneAccess.Denied,
                AppCapabilityAccessStatus.NotDeclaredByApp => MicrophoneAccess.Unavailable,
                _ => MicrophoneAccess.Allowed
            };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the microphone access status.");
            return MicrophoneAccess.Allowed;
        }
    }

    public Task<MicrophoneSessionResult> StartLevelMonitorAsync(string? deviceId, Action<double> levelChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(levelChanged);
        return OpenAsync(deviceId, levelChanged, null, 0, cancellationToken);
    }

    public Task<MicrophoneSessionResult> StartCaptureAsync(
        string? deviceId,
        int sampleRate,
        Action<ReadOnlyMemory<byte>> audioAvailable,
        Action<double> levelChanged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioAvailable);
        ArgumentNullException.ThrowIfNull(levelChanged);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        return OpenAsync(deviceId, levelChanged, audioAvailable, sampleRate, cancellationToken);
    }

    private async Task<MicrophoneSessionResult> OpenAsync(
        string? deviceId,
        Action<double> levelChanged,
        Action<ReadOnlyMemory<byte>>? audioAvailable,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        AudioGraph? graph = null;
        try
        {
            var graphResult = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech)).AsTask(cancellationToken).ConfigureAwait(false);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                Log.Warning("Could not create the microphone graph: {Status}.", graphResult.Status);
                return MicrophoneSessionResult.Failed(MicrophoneAccess.Unavailable);
            }

            graph = graphResult.Graph;

            var device = await TryGetDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
            var inputResult = device is null
                ? await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech).AsTask(cancellationToken).ConfigureAwait(false)
                : await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, graph.EncodingProperties, device).AsTask(cancellationToken).ConfigureAwait(false);

            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                var access = inputResult.Status switch
                {
                    AudioDeviceNodeCreationStatus.AccessDenied => MicrophoneAccess.Denied,
                    AudioDeviceNodeCreationStatus.DeviceNotAvailable => MicrophoneAccess.NoDevice,
                    _ => MicrophoneAccess.Unavailable
                };

                if (access == MicrophoneAccess.Unavailable)
                    Log.Warning(inputResult.ExtendedError, "Could not open the microphone: {Status}.", inputResult.Status);

                graph.Dispose();
                return MicrophoneSessionResult.Failed(access);
            }

            var output = graph.CreateFrameOutputNode();
            inputResult.DeviceInputNode.AddOutgoingConnection(output);

            var session = new GraphSession(graph, output, levelChanged, audioAvailable, sampleRate);
            graph.Start();
            return new MicrophoneSessionResult(MicrophoneAccess.Allowed, session);
        }
        catch (OperationCanceledException)
        {
            graph?.Dispose();
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            graph?.Dispose();
            return MicrophoneSessionResult.Failed(MicrophoneAccess.Denied);
        }
        catch (Exception ex)
        {
            graph?.Dispose();
            Log.Warning(ex, "Could not open the microphone.");
            return MicrophoneSessionResult.Failed(MicrophoneAccess.Unavailable);
        }
    }

    public void Dispose()
    {
        lock (_watcherLock)
        {
            _isDisposed = true;
            _devicesChanged = null;
            StopWatching();
        }
    }

    private static string GetDefaultCaptureId()
    {
        try
        {
            return MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default) ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the default microphone.");
            return string.Empty;
        }
    }

    private static async Task<DeviceInformation?> TryGetDeviceAsync(string? deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        try
        {
            var device = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(cancellationToken).ConfigureAwait(false);
            return device is { IsEnabled: true } ? device : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The selected microphone is no longer available. Using the system default.");
            return null;
        }
    }

    private void StartWatching()
    {
        if (_isDisposed || _watcher is not null)
            return;

        try
        {
            var watcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioCaptureSelector());
            watcher.Added += WatcherAdded;
            watcher.Removed += WatcherRemoved;
            watcher.Updated += WatcherUpdated;
            watcher.EnumerationCompleted += WatcherEnumerationCompleted;
            _isEnumerationCompleted = false;
            watcher.Start();
            _watcher = watcher;
            MediaDevice.DefaultAudioCaptureDeviceChanged += DefaultCaptureDeviceChanged;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not watch for microphone changes.");
        }
    }

    private void StopWatching()
    {
        var watcher = _watcher;
        if (watcher is null)
            return;

        _watcher = null;
        MediaDevice.DefaultAudioCaptureDeviceChanged -= DefaultCaptureDeviceChanged;
        watcher.Added -= WatcherAdded;
        watcher.Removed -= WatcherRemoved;
        watcher.Updated -= WatcherUpdated;
        watcher.EnumerationCompleted -= WatcherEnumerationCompleted;

        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not stop the microphone watcher.");
        }
    }

    private void WatcherAdded(DeviceWatcher sender, DeviceInformation args) => RaiseDevicesChanged(sender);

    private void WatcherRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => RaiseDevicesChanged(sender);

    private void WatcherUpdated(DeviceWatcher sender, DeviceInformationUpdate args) => RaiseDevicesChanged(sender);

    private void WatcherEnumerationCompleted(DeviceWatcher sender, object args)
    {
        lock (_watcherLock)
        {
            if (ReferenceEquals(sender, _watcher))
                _isEnumerationCompleted = true;
        }
    }

    private void DefaultCaptureDeviceChanged(object sender, DefaultAudioCaptureDeviceChangedEventArgs args) => RaiseDevicesChanged(_watcher);

    private void RaiseDevicesChanged(DeviceWatcher? sender)
    {
        EventHandler? handler;
        lock (_watcherLock)
        {
            // The watcher reports every existing device as added before EnumerationCompleted.
            if (sender is null || !ReferenceEquals(sender, _watcher) || !_isEnumerationCompleted)
                return;

            handler = _devicesChanged;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A microphone change handler failed.");
        }
    }

    /// <summary>
    /// Reads each audio quantum of an open microphone: reports a smoothed peak level about 30 times a second and, for a
    /// capture, downmixes, resamples and converts the samples to 16-bit PCM in chunks of about 100 ms.
    /// </summary>
    private sealed class GraphSession : IDisposable
    {
        private static readonly Guid MemoryBufferByteAccessId = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
        private const double ReportIntervalSeconds = 1.0 / 30;
        private const double ReleasePerSecond = 1.6;
        private const double ChunkSeconds = 0.1;

        private readonly Lock _lock = new();
        private readonly AudioGraph _graph;
        private readonly AudioFrameOutputNode _output;
        private readonly Action<double> _levelChanged;
        private readonly Action<ReadOnlyMemory<byte>>? _audioAvailable;
        private readonly bool _isFloat;
        private readonly int _channels;
        private readonly double _secondsPerQuantum;
        private readonly double _sourceStep;
        private readonly short[] _chunk;
        private float[] _mono = [];
        private int _chunkLength;
        private double _resamplePosition;
        private float _previousSample;
        private double _level;
        private double _sinceReport;
        private bool _isDisposed;

        public GraphSession(AudioGraph graph, AudioFrameOutputNode output, Action<double> levelChanged, Action<ReadOnlyMemory<byte>>? audioAvailable, int sampleRate)
        {
            _graph = graph;
            _output = output;
            _levelChanged = levelChanged;
            _audioAvailable = audioAvailable;

            var encoding = graph.EncodingProperties;
            var sourceRate = encoding.SampleRate > 0 ? encoding.SampleRate : 48000u;
            _isFloat = string.Equals(encoding.Subtype, MediaEncodingSubtypes.Float, StringComparison.OrdinalIgnoreCase) && encoding.BitsPerSample == 32;
            _channels = (int)Math.Max(1u, encoding.ChannelCount);
            _secondsPerQuantum = graph.SamplesPerQuantum / (double)sourceRate;
            _sourceStep = sampleRate > 0 ? sourceRate / (double)sampleRate : 1;
            _chunk = new short[Math.Max(1, (int)(sampleRate * ChunkSeconds))];

            if (!_isFloat)
                Log.Warning("The microphone graph uses {Subtype} {Bits}-bit samples. It records silence.", encoding.Subtype, encoding.BitsPerSample);

            graph.QuantumStarted += GraphQuantumStarted;
            graph.UnrecoverableErrorOccurred += GraphUnrecoverableErrorOccurred;
        }

        public void Dispose()
        {
            ReadOnlyMemory<byte> last;
            lock (_lock)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                last = TakeChunk();
            }

            _graph.QuantumStarted -= GraphQuantumStarted;
            _graph.UnrecoverableErrorOccurred -= GraphUnrecoverableErrorOccurred;

            try
            {
                _graph.Stop();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not stop the microphone graph.");
            }

            _graph.Dispose();

            if (!last.IsEmpty)
                Deliver(last);
        }

        private void GraphUnrecoverableErrorOccurred(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
        {
            Log.Warning("The microphone graph stopped: {Error}.", args.Error);
            Report(0);
        }

        private void GraphQuantumStarted(AudioGraph sender, object args)
        {
            ReadOnlyMemory<byte> chunk = default;
            double level;
            var report = false;

            lock (_lock)
            {
                if (_isDisposed)
                    return;

                double peak;
                try
                {
                    peak = ReadQuantum(ref chunk);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not read a microphone frame.");
                    return;
                }

                _level = Math.Max(peak, _level - ReleasePerSecond * _secondsPerQuantum);
                _sinceReport += _secondsPerQuantum;
                if (_sinceReport >= ReportIntervalSeconds)
                {
                    _sinceReport = 0;
                    report = true;
                }

                level = _level;
            }

            if (!chunk.IsEmpty)
                Deliver(chunk);

            if (report)
                Report(level);
        }

        private unsafe double ReadQuantum(ref ReadOnlyMemory<byte> chunk)
        {
            using var frame = _output.GetFrame();
            using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using var reference = buffer.CreateReference();

            if (!_isFloat)
                return 0;

            var objectReference = ((IWinRTObject)reference).NativeObject;
            var iid = MemoryBufferByteAccessId;
            if (Marshal.QueryInterface(objectReference.ThisPtr, in iid, out var access) != 0 || access == 0)
                return 0;

            try
            {
                byte* data;
                uint capacity;

                // IMemoryBufferByteAccess::GetBuffer is the first method after IUnknown.
                var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(void***)access)[3];
                if (getBuffer(access, &data, &capacity) != 0 || data is null)
                    return 0;

                var samples = new ReadOnlySpan<float>(data, (int)Math.Min(capacity, buffer.Length) / sizeof(float));
                var peak = 0f;
                foreach (var sample in samples)
                {
                    var magnitude = Math.Abs(sample);
                    if (magnitude > peak)
                        peak = magnitude;
                }

                if (_audioAvailable is not null)
                    chunk = Resample(samples);

                // A speaking voice peaks well below full scale; this curve makes normal speech fill most of the meter.
                return Math.Clamp(Math.Sqrt(peak) * 1.4, 0, 1);
            }
            finally
            {
                Marshal.Release(access);
            }
        }

        /// <summary>
        /// Downmixes the interleaved quantum to mono and resamples it by linear interpolation, carrying the position and
        /// the last sample into the next quantum so chunk borders stay continuous. Returns a full chunk when one filled up.
        /// </summary>
        private ReadOnlyMemory<byte> Resample(ReadOnlySpan<float> interleaved)
        {
            var frames = interleaved.Length / _channels;
            if (frames == 0)
                return default;

            if (_mono.Length < frames)
                _mono = new float[frames];

            for (var frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                    sum += interleaved[(frameIndex * _channels) + channel];

                _mono[frameIndex] = sum / _channels;
            }

            ReadOnlyMemory<byte> full = default;
            while (_resamplePosition < frames - 1)
            {
                var index = (int)Math.Floor(_resamplePosition);
                var fraction = (float)(_resamplePosition - index);
                var start = index < 0 ? _previousSample : _mono[index];
                var end = _mono[index + 1];
                var value = start + ((end - start) * fraction);

                _chunk[_chunkLength++] = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
                if (_chunkLength == _chunk.Length)
                    full = TakeChunk();

                _resamplePosition += _sourceStep;
            }

            _resamplePosition -= frames;
            _previousSample = _mono[frames - 1];
            return full;
        }

        private ReadOnlyMemory<byte> TakeChunk()
        {
            if (_chunkLength == 0)
                return default;

            var bytes = new byte[_chunkLength * sizeof(short)];
            for (var index = 0; index < _chunkLength; index++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short)), _chunk[index]);

            _chunkLength = 0;
            return bytes;
        }

        private void Deliver(ReadOnlyMemory<byte> chunk)
        {
            try
            {
                _audioAvailable?.Invoke(chunk);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "A microphone audio handler failed.");
            }
        }

        private void Report(double level)
        {
            try
            {
                _levelChanged(level);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "A microphone level handler failed.");
            }
        }
    }
}
