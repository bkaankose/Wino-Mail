using System.Text;

namespace Wino.NotificationHost.Contracts;

public static class NotificationHostCodec
{
    private const uint Magic = 0x4F484E57; // WNHO
    private const ushort Version = 1;
    private const byte RequestKind = 1;
    private const byte ActivationKind = 2;
    private const int MaximumPayloadBytes = 256 * 1024;
    private const int MaximumMetadataBytes = 4 * 1024;
    private const int MaximumUserInputCount = 16;

    public static byte[] EncodeRequest(NotificationHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteHeader(writer, RequestKind, request.CreatedAtUtc);
        writer.Write((byte)request.Operation);
        writer.Write((byte)request.Application);
        WriteString(writer, request.Payload, MaximumPayloadBytes);
        WriteString(writer, request.Tag, MaximumMetadataBytes);
        WriteString(writer, request.Group, MaximumMetadataBytes);
        writer.Flush();
        return stream.ToArray();
    }

    public static NotificationHostRequest DecodeRequest(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var createdAtUtc = ReadHeader(reader, RequestKind);
        var operation = (NotificationHostOperation)reader.ReadByte();
        var application = (NotificationHostApplication)reader.ReadByte();
        var request = new NotificationHostRequest(
            createdAtUtc,
            operation,
            application,
            ReadString(reader, MaximumPayloadBytes),
            ReadString(reader, MaximumMetadataBytes),
            ReadString(reader, MaximumMetadataBytes));

        EnsureFullyConsumed(stream);
        ValidateRequest(request);
        return request;
    }

    public static byte[] EncodeActivation(NotificationHostActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ValidateApplication(activation.Application);
        ArgumentException.ThrowIfNullOrWhiteSpace(activation.Argument);

        if (activation.UserInput.Count > MaximumUserInputCount)
            throw new InvalidDataException($"Notification activation has more than {MaximumUserInputCount} input values.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteHeader(writer, ActivationKind, activation.CreatedAtUtc);
        writer.Write((byte)activation.Application);
        WriteString(writer, activation.Argument, MaximumMetadataBytes);
        writer.Write((byte)activation.UserInput.Count);

        foreach (var pair in activation.UserInput.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteString(writer, pair.Key, MaximumMetadataBytes);
            WriteString(writer, pair.Value, MaximumMetadataBytes);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static NotificationHostActivation DecodeActivation(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var createdAtUtc = ReadHeader(reader, ActivationKind);
        var application = (NotificationHostApplication)reader.ReadByte();
        var argument = ReadString(reader, MaximumMetadataBytes)
            ?? throw new InvalidDataException("Notification activation argument is missing.");
        var count = reader.ReadByte();

        if (count > MaximumUserInputCount)
            throw new InvalidDataException($"Notification activation has more than {MaximumUserInputCount} input values.");

        var userInput = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = ReadString(reader, MaximumMetadataBytes)
                ?? throw new InvalidDataException("Notification activation input key is missing.");
            var value = ReadString(reader, MaximumMetadataBytes)
                ?? throw new InvalidDataException("Notification activation input value is missing.");

            if (!userInput.TryAdd(key, value))
                throw new InvalidDataException("Notification activation contains a duplicate input key.");
        }

        EnsureFullyConsumed(stream);
        ValidateApplication(application);
        return new NotificationHostActivation(createdAtUtc, application, argument, userInput);
    }

    private static void ValidateRequest(NotificationHostRequest request)
    {
        ValidateApplication(request.Application);

        if (!Enum.IsDefined(request.Operation))
            throw new InvalidDataException("Unknown notification host operation.");

        switch (request.Operation)
        {
            case NotificationHostOperation.Show when string.IsNullOrWhiteSpace(request.Payload):
                throw new InvalidDataException("Show requests require a notification payload.");
            case NotificationHostOperation.RemoveByTag when string.IsNullOrWhiteSpace(request.Tag):
            case NotificationHostOperation.RemoveByTagAndGroup when string.IsNullOrWhiteSpace(request.Tag) || string.IsNullOrWhiteSpace(request.Group):
            case NotificationHostOperation.RemoveGroup when string.IsNullOrWhiteSpace(request.Group):
                throw new InvalidDataException("The notification removal request is missing its tag or group.");
        }

        ValidateStringSize(request.Payload, MaximumPayloadBytes);
        ValidateStringSize(request.Tag, MaximumMetadataBytes);
        ValidateStringSize(request.Group, MaximumMetadataBytes);
    }

    private static void ValidateApplication(NotificationHostApplication application)
    {
        if (!Enum.IsDefined(application))
            throw new InvalidDataException("Unknown notification host application.");
    }

    private static void WriteHeader(BinaryWriter writer, byte kind, DateTimeOffset createdAtUtc)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(kind);
        writer.Write(createdAtUtc.ToUniversalTime().Ticks);
    }

    private static DateTimeOffset ReadHeader(BinaryReader reader, byte expectedKind)
    {
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Invalid notification host envelope signature.");

        if (reader.ReadUInt16() != Version)
            throw new InvalidDataException("Unsupported notification host envelope version.");

        if (reader.ReadByte() != expectedKind)
            throw new InvalidDataException("Unexpected notification host envelope kind.");

        try
        {
            return new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Invalid notification host envelope timestamp.", ex);
        }
    }

    private static void WriteString(BinaryWriter writer, string? value, int maximumBytes)
    {
        if (value == null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > maximumBytes)
            throw new InvalidDataException($"Notification host string exceeds {maximumBytes} UTF-8 bytes.");

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length == -1)
            return null;

        if (length < 0 || length > maximumBytes)
            throw new InvalidDataException("Notification host string length is invalid.");

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Notification host envelope ended inside a string.");

        return Encoding.UTF8.GetString(bytes);
    }

    private static void ValidateStringSize(string? value, int maximumBytes)
    {
        if (value != null && Encoding.UTF8.GetByteCount(value) > maximumBytes)
            throw new InvalidDataException($"Notification host string exceeds {maximumBytes} UTF-8 bytes.");
    }

    private static void EnsureFullyConsumed(Stream stream)
    {
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Notification host envelope contains trailing data.");
    }
}
