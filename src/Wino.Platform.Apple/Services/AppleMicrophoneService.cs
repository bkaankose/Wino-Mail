using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AudioToolbox;
using AVFoundation;
using CoreFoundation;
using CoreMedia;
using Foundation;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// Lists the microphones and reads them through an <see cref="AVCaptureSession"/>, which opens any device by its
/// unique id on macOS and iOS. Access goes through the system privacy prompt: the app needs
/// NSMicrophoneUsageDescription and, in the sandbox, the audio-input entitlement, or the system denies silently.
/// </summary>
public sealed class AppleMicrophoneService : IMicrophoneService, IDisposable
{
    private readonly Lock _watchLock = new();
    private EventHandler? _devicesChanged;
    private NSObject? _connectedObserver;
    private NSObject? _disconnectedObserver;
#if __MACOS__
    private GCHandle _listenerHandle;
#endif
    private bool _isDisposed;

    public event EventHandler? DevicesChanged
    {
        add
        {
            lock (_watchLock)
            {
                _devicesChanged += value;
                StartWatching();
            }
        }
        remove
        {
            lock (_watchLock)
            {
                _devicesChanged -= value;
                if (_devicesChanged is null)
                    StopWatching();
            }
        }
    }

    public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<MicrophoneDevice>();

        try
        {
            var defaultId = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio)?.UniqueID;
            foreach (var device in GetAudioDevices())
            {
                if (!device.Connected || string.IsNullOrEmpty(device.UniqueID))
                    continue;

                devices.Add(new MicrophoneDevice(device.UniqueID, device.LocalizedName ?? string.Empty, string.Equals(device.UniqueID, defaultId, StringComparison.Ordinal)));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not list the microphones.");
        }

        return Task.FromResult<IReadOnlyList<MicrophoneDevice>>(devices);
    }

    public MicrophoneAccess GetAccessStatus()
    {
        try
        {
            return AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio) switch
            {
                AVAuthorizationStatus.Denied or AVAuthorizationStatus.Restricted => MicrophoneAccess.Denied,
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

    private static async Task<MicrophoneSessionResult> OpenAsync(
        string? deviceId,
        Action<double> levelChanged,
        Action<ReadOnlyMemory<byte>>? audioAvailable,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio))
            {
                case AVAuthorizationStatus.Denied or AVAuthorizationStatus.Restricted:
                    return MicrophoneSessionResult.Failed(MicrophoneAccess.Denied);
                case AVAuthorizationStatus.NotDetermined:
                    if (!await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio).ConfigureAwait(false))
                        return MicrophoneSessionResult.Failed(MicrophoneAccess.Denied);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var device = FindDevice(deviceId) ?? AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio);
            if (device is null)
                return MicrophoneSessionResult.Failed(MicrophoneAccess.NoDevice);

            // StartRunning blocks until the device is open, so the session starts off the calling thread.
            return await Task.Run(() => CaptureSession.Start(device, levelChanged, audioAvailable, sampleRate), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the microphone.");
            return MicrophoneSessionResult.Failed(MicrophoneAccess.Unavailable);
        }
    }

    public void Dispose()
    {
        lock (_watchLock)
        {
            _isDisposed = true;
            _devicesChanged = null;
            StopWatching();
        }
    }

    private static AVCaptureDevice[] GetAudioDevices()
    {
        using var discovery = AVCaptureDeviceDiscoverySession.Create(
            [AVCaptureDeviceType.BuiltInMicrophone, AVCaptureDeviceType.External],
            AVMediaTypes.Audio,
            AVCaptureDevicePosition.Unspecified);
        return discovery?.Devices ?? [];
    }

    private static AVCaptureDevice? FindDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var device = AVCaptureDevice.DeviceWithUniqueID(deviceId);
        if (device is { Connected: true })
            return device;

        Log.Debug("The selected microphone is no longer available. Using the system default.");
        return null;
    }

    private void StartWatching()
    {
        if (_isDisposed || _connectedObserver is not null)
            return;

        try
        {
            _connectedObserver = AVCaptureDevice.Notifications.ObserveWasConnected((_, _) => RaiseDevicesChanged());
            _disconnectedObserver = AVCaptureDevice.Notifications.ObserveWasDisconnected((_, _) => RaiseDevicesChanged());
#if __MACOS__
            _listenerHandle = GCHandle.Alloc(this);
            unsafe
            {
                var address = DefaultInputDeviceAddress;
                var status = CoreAudio.AudioObjectAddPropertyListener(CoreAudio.SystemObject, &address, &DefaultInputDeviceChanged, GCHandle.ToIntPtr(_listenerHandle));
                if (status != 0)
                    Log.Debug("Could not watch the default microphone: {Status}.", status);
            }
#endif
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not watch for microphone changes.");
        }
    }

    private void StopWatching()
    {
        if (_connectedObserver is null)
            return;

        _connectedObserver.Dispose();
        _disconnectedObserver?.Dispose();
        _connectedObserver = null;
        _disconnectedObserver = null;

#if __MACOS__
        if (_listenerHandle.IsAllocated)
        {
            unsafe
            {
                var address = DefaultInputDeviceAddress;
                CoreAudio.AudioObjectRemovePropertyListener(CoreAudio.SystemObject, &address, &DefaultInputDeviceChanged, GCHandle.ToIntPtr(_listenerHandle));
            }

            _listenerHandle.Free();
        }
#endif
    }

    private void RaiseDevicesChanged()
    {
        EventHandler? handler;
        lock (_watchLock)
            handler = _devicesChanged;

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A microphone change handler failed.");
        }
    }

#if __MACOS__
    private static readonly CoreAudio.PropertyAddress DefaultInputDeviceAddress = new(CoreAudio.DefaultInputDeviceSelector, CoreAudio.GlobalScope, CoreAudio.MainElement);

    [UnmanagedCallersOnly]
    private static unsafe int DefaultInputDeviceChanged(uint objectId, uint addressCount, CoreAudio.PropertyAddress* addresses, nint clientData)
    {
        try
        {
            if (GCHandle.FromIntPtr(clientData).Target is AppleMicrophoneService service)
                service.RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "A default microphone change could not be reported.");
        }

        return 0;
    }

    /// <summary>
    /// AVFoundation reports connected and removed microphones but not a new system default; CoreAudio's default input
    /// property does.
    /// </summary>
    private static unsafe class CoreAudio
    {
        private const string Library = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

        public const uint SystemObject = 1;
        public const uint DefaultInputDeviceSelector = 0x64496E20; // 'dIn '
        public const uint GlobalScope = 0x676C6F62; // 'glob'
        public const uint MainElement = 0;

        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct PropertyAddress(uint Selector, uint Scope, uint Element);

        [DllImport(Library)]
        public static extern int AudioObjectAddPropertyListener(uint objectId, PropertyAddress* address, delegate* unmanaged<uint, uint, PropertyAddress*, nint, int> listener, nint clientData);

        [DllImport(Library)]
        public static extern int AudioObjectRemovePropertyListener(uint objectId, PropertyAddress* address, delegate* unmanaged<uint, uint, PropertyAddress*, nint, int> listener, nint clientData);
    }
#endif

    /// <summary>
    /// One open microphone: reports a smoothed peak level about 30 times a second and, for a capture, downmixes,
    /// resamples and converts the samples to 16-bit PCM in chunks of about 100 ms. Sample buffers arrive on a serial
    /// queue in whatever linear PCM layout the device uses; on macOS the output asks CoreAudio for mono float at the
    /// target rate, so the resampler usually passes samples through.
    /// </summary>
    private sealed class CaptureSession : NSObject, IAVCaptureAudioDataOutputSampleBufferDelegate, IDisposable
    {
        private const double ReportIntervalSeconds = 1.0 / 30;
        private const double ReleasePerSecond = 1.6;
        private const double ChunkSeconds = 0.1;

        private readonly Lock _lock = new();
        private readonly AVCaptureSession _session;
        private readonly AVCaptureAudioDataOutput _output;
        private readonly DispatchQueue _queue;
        private readonly Action<double> _levelChanged;
        private readonly Action<ReadOnlyMemory<byte>>? _audioAvailable;
        private readonly int _sampleRate;
        private readonly short[] _chunk;
        private byte[] _raw = [];
        private float[] _mono = [];
        private int _chunkLength;
        private double _resamplePosition;
        private float _previousSample;
        private double _level;
        private double _sinceReport;
        private bool _isStopped;

        private CaptureSession(AVCaptureSession session, AVCaptureAudioDataOutput output, Action<double> levelChanged, Action<ReadOnlyMemory<byte>>? audioAvailable, int sampleRate)
        {
            _session = session;
            _output = output;
            _levelChanged = levelChanged;
            _audioAvailable = audioAvailable;
            _sampleRate = sampleRate;
            _chunk = new short[Math.Max(1, (int)(sampleRate * ChunkSeconds))];
            _queue = new DispatchQueue("com.winomail.microphone");
        }

        public static MicrophoneSessionResult Start(AVCaptureDevice device, Action<double> levelChanged, Action<ReadOnlyMemory<byte>>? audioAvailable, int sampleRate)
        {
            var input = AVCaptureDeviceInput.FromDevice(device, out var error);
            if (input is null)
            {
                Log.Warning("Could not open the microphone {Name}: {Error}.", device.LocalizedName, error?.LocalizedDescription);
                return MicrophoneSessionResult.Failed(MicrophoneAccess.Unavailable);
            }

            var session = new AVCaptureSession();
            var output = new AVCaptureAudioDataOutput();
#if __MACOS__
            output.AudioSettings = new AudioSettings
            {
                Format = AudioFormatType.LinearPCM,
                LinearPcmFloat = true,
                LinearPcmBitDepth = 32,
                LinearPcmBigEndian = false,
                LinearPcmNonInterleaved = false,
                NumberChannels = 1,
                SampleRate = sampleRate > 0 ? sampleRate : 16000
            };
#endif

            session.BeginConfiguration();
            if (!session.CanAddInput(input) || !session.CanAddOutput(output))
            {
                session.CommitConfiguration();
                Log.Warning("The microphone {Name} cannot be added to a capture session.", device.LocalizedName);
                input.Dispose();
                output.Dispose();
                session.Dispose();
                return MicrophoneSessionResult.Failed(MicrophoneAccess.Unavailable);
            }

            session.AddInput(input);
            session.AddOutput(output);
            session.CommitConfiguration();

            var capture = new CaptureSession(session, output, levelChanged, audioAvailable, sampleRate);
            output.SetSampleBufferDelegate(capture, capture._queue);
            session.StartRunning();

            if (!session.Running)
            {
                // Without access the system starts the session and stops it at once; it never reports an error.
                capture.Dispose();
                return MicrophoneSessionResult.Failed(
                    AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio) == AVAuthorizationStatus.Authorized
                        ? MicrophoneAccess.Unavailable
                        : MicrophoneAccess.Denied);
            }

            return new MicrophoneSessionResult(MicrophoneAccess.Allowed, capture);
        }

        [Export("captureOutput:didOutputSampleBuffer:fromConnection:")]
        public void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            ReadOnlyMemory<byte> chunk = default;
            double level;
            var report = false;

            try
            {
                lock (_lock)
                {
                    if (_isStopped)
                        return;

                    if (!TryRead(sampleBuffer, out var frames, out var sourceRate))
                        return;

                    var peak = 0f;
                    foreach (var sample in _mono.AsSpan(0, frames))
                        peak = Math.Max(peak, Math.Abs(sample));

                    if (_audioAvailable is not null)
                        chunk = Resample(frames, sourceRate);

                    var seconds = frames / sourceRate;
                    // A speaking voice peaks well below full scale; this curve makes normal speech fill most of the meter.
                    _level = Math.Max(Math.Clamp(Math.Sqrt(peak) * 1.4, 0, 1), _level - ReleasePerSecond * seconds);
                    _sinceReport += seconds;
                    if (_sinceReport >= ReportIntervalSeconds)
                    {
                        _sinceReport = 0;
                        report = true;
                    }

                    level = _level;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read a microphone buffer.");
                return;
            }
            finally
            {
                sampleBuffer.Dispose();
            }

            if (!chunk.IsEmpty)
                Deliver(chunk);

            if (report)
                Report(level);
        }

        /// <summary>Reads the buffer's linear PCM into <see cref="_mono"/>, averaging the channels.</summary>
        private unsafe bool TryRead(CMSampleBuffer sampleBuffer, out int frames, out double sourceRate)
        {
            frames = 0;
            sourceRate = 0;

            var format = sampleBuffer.GetAudioFormatDescription()?.AudioStreamBasicDescription;
            if (format is not { } description || description.Format != AudioFormatType.LinearPCM || description.FormatFlags.HasFlag(AudioFormatFlags.IsBigEndian))
                return false;

            var bits = description.BitsPerChannel;
            var isFloat = description.FormatFlags.HasFlag(AudioFormatFlags.IsFloat);
            if (!(isFloat ? bits == 32 : bits is 16 or 32))
                return false;

            using var block = sampleBuffer.GetDataBuffer();
            if (block is null || block.DataLength == 0)
                return false;

            var length = (int)block.DataLength;
            if (_raw.Length < length)
                _raw = new byte[length];

            fixed (byte* destination = _raw)
            {
                if (block.CopyDataBytes(0, (nuint)length, (nint)destination) != CMBlockBufferError.None)
                    return false;
            }

            var channels = Math.Max(1, description.ChannelsPerFrame);
            var bytesPerSample = bits / 8;
            var nonInterleaved = description.FormatFlags.HasFlag(AudioFormatFlags.IsNonInterleaved);
            frames = length / (bytesPerSample * channels);
            if (frames == 0)
                return false;

            if (_mono.Length < frames)
                _mono = new float[frames];

            var raw = _raw.AsSpan(0, length);
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = nonInterleaved ? (channel * frames) + frame : (frame * channels) + channel;
                    var bytes = raw.Slice(index * bytesPerSample, bytesPerSample);
                    sum += isFloat
                        ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
                        : bits == 16
                            ? BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f
                            : BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f;
                }

                _mono[frame] = sum / channels;
            }

            sourceRate = description.SampleRate > 0 ? description.SampleRate : _sampleRate;
            return sourceRate > 0;
        }

        /// <summary>
        /// Resamples the mono frames by linear interpolation, carrying the position and the last sample into the next
        /// buffer so chunk borders stay continuous. Returns a full chunk when one filled up.
        /// </summary>
        private ReadOnlyMemory<byte> Resample(int frames, double sourceRate)
        {
            var step = sourceRate / _sampleRate;
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

                _resamplePosition += step;
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Stop();

            base.Dispose(disposing);
        }

        /// <summary>Closes the microphone, then delivers the last partial chunk.</summary>
        private void Stop()
        {
            lock (_lock)
            {
                if (_isStopped)
                    return;
            }

            try
            {
                _session.StopRunning();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not stop the microphone session.");
            }

            ReadOnlyMemory<byte> last;
            lock (_lock)
            {
                _isStopped = true;
                last = TakeChunk();
            }

            _output.SetSampleBufferDelegate(null, null);
            foreach (var input in _session.Inputs)
                _session.RemoveInput(input);
            _session.RemoveOutput(_output);
            _output.Dispose();
            _session.Dispose();
            _queue.Dispose();

            if (!last.IsEmpty)
                Deliver(last);
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
