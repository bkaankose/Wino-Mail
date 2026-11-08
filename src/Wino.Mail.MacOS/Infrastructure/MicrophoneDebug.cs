#if DEBUG
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Bridge commands for the voice microphone: "mic-devices", "mic-access", and "mic-capture [seconds] [deviceId]", which
/// records 16 kHz mono PCM to mic-capture.wav in the bridge folder and reports chunks, duration and peak level.
/// </summary>
internal static class MicrophoneDebug
{
    public static void Register(IServiceProvider services)
    {
        MacDebugBridge.Register("mic-devices", async _ =>
        {
            var devices = await services.GetRequiredService<IMicrophoneService>().GetDevicesAsync();
            return devices.Count == 0
                ? "none"
                : string.Join("\n", devices.Select(device => $"{(device.IsDefault ? "*" : " ")} {device.Name} | {device.Id}"));
        });

        MacDebugBridge.Register("mic-access", _ => Task.FromResult(
            $"{services.GetRequiredService<IMicrophoneService>().GetAccessStatus()} system={AVFoundation.AVCaptureDevice.GetAuthorizationStatus(AVFoundation.AVAuthorizationMediaType.Audio)}"));

        MacDebugBridge.Register("mic-capture", async args =>
        {
            var seconds = args.Length > 0 && double.TryParse(args[0], out var value) ? value : 3;
            var deviceId = args.Length > 1 ? args[1] : null;
            const int sampleRate = 16000;

            var chunks = 0;
            var levels = 0;
            var peak = 0.0;
            using var pcm = new MemoryStream();
            var gate = new object();

            var result = await services.GetRequiredService<IMicrophoneService>().StartCaptureAsync(
                deviceId,
                sampleRate,
                chunk => { lock (gate) { chunks++; pcm.Write(chunk.Span); } },
                level => { lock (gate) { levels++; peak = Math.Max(peak, level); } });

            if (result.Session is not { } session)
                return $"access={result.Access}";

            await Task.Delay(TimeSpan.FromSeconds(seconds));
            session.Dispose();

            byte[] data;
            lock (gate)
                data = pcm.ToArray();

            var path = Path.Combine(Path.GetTempPath(), "wino-debug", "mic-capture.wav");
            await File.WriteAllBytesAsync(path, ToWave(data, sampleRate));
            return $"access={result.Access} chunks={chunks} bytes={data.Length} audioSeconds={data.Length / 2.0 / sampleRate:0.00} " +
                   $"levelReports={levels} peakLevel={peak:0.00} wav={path}";
        });
    }

    private static byte[] ToWave(byte[] pcm, int sampleRate)
    {
        var wave = new byte[44 + pcm.Length];
        var span = wave.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[44..]);
        return wave;
    }
}
#endif
