using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MimeKit;

namespace Wino.Core.Domain.Extensions;

public static class ReadReceiptExtensions
{
    public static bool HasReadReceiptRequest(this MimeMessage mimeMessage)
        => mimeMessage?.Headers?.Contains(Constants.DispositionNotificationToHeader) == true
           && !string.IsNullOrWhiteSpace(mimeMessage.Headers[Constants.DispositionNotificationToHeader]);

    public static void SetReadReceiptRequest(this MimeMessage mimeMessage, string address, bool isRequested)
    {
        if (mimeMessage == null)
            return;

        mimeMessage.Headers.Remove(Constants.DispositionNotificationToHeader);

        if (isRequested && !string.IsNullOrWhiteSpace(address))
        {
            mimeMessage.Headers.Add(Constants.DispositionNotificationToHeader, address.Trim());
        }
    }

    public static bool LooksLikeReadReceipt(this MimeMessage mimeMessage)
    {
        if (mimeMessage?.Body == null)
            return false;

        return mimeMessage.BodyParts.Any(IsReadReceiptEntity) || IsReadReceiptEntity(mimeMessage.Body);
    }

    public static ReadReceiptParseResult ParseReadReceipt(this MimeMessage mimeMessage)
    {
        if (!mimeMessage.LooksLikeReadReceipt())
            return ReadReceiptParseResult.Empty;

        var entity = mimeMessage.BodyParts.FirstOrDefault(IsReadReceiptEntity) ?? mimeMessage.Body;
        var lines = ReadEntityLines(entity);

        string originalMessageId = null;

        foreach (var line in lines)
        {
            if (line.StartsWith(Constants.OriginalMessageIdHeader + ":", StringComparison.OrdinalIgnoreCase))
            {
                originalMessageId = line.Substring(line.IndexOf(':') + 1).Trim();
                break;
            }
        }

        var acknowledgedAtUtc = mimeMessage.Date != DateTimeOffset.MinValue
            ? mimeMessage.Date.UtcDateTime
            : (DateTime?)null;

        return new ReadReceiptParseResult(
            true,
            MailHeaderExtensions.NormalizeMessageId(originalMessageId),
            acknowledgedAtUtc);
    }

    private static bool IsReadReceiptEntity(MimeEntity entity)
    {
        if (entity?.ContentType == null)
            return false;

        if (entity.ContentType.MimeType.Equals("message/disposition-notification", StringComparison.OrdinalIgnoreCase))
            return true;

        var reportType = entity.ContentType.Parameters["report-type"];
        return entity.ContentType.MimeType.Equals("multipart/report", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(reportType)
               && reportType.Equals("disposition-notification", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadEntityLines(MimeEntity entity)
    {
        if (entity is TextPart textPart)
        {
            return SplitLines(textPart.Text);
        }

        if (entity is MimePart mimePart)
        {
            using var memoryStream = new MemoryStream();
            mimePart.Content?.DecodeTo(memoryStream);
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream);
            return SplitLines(reader.ReadToEnd());
        }

        using var serializedStream = new MemoryStream();
        entity.WriteTo(serializedStream);
        serializedStream.Position = 0;
        using var serializedReader = new StreamReader(serializedStream);
        return SplitLines(serializedReader.ReadToEnd());
    }

    private static IEnumerable<string> SplitLines(string content)
        => string.IsNullOrWhiteSpace(content)
            ? []
            : content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
}

public sealed record ReadReceiptParseResult(bool IsReadReceipt, string OriginalMessageId, DateTime? AcknowledgedAtUtc)
{
    public static ReadReceiptParseResult Empty { get; } = new(false, string.Empty, null);
}
