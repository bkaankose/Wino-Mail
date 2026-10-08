using System;
using System.IO;

namespace Wino.Core.Domain.Extensions;

public static class MimeExtensions
{
    public static string GetBase64MimeMessage(this MimeKit.MimeMessage message)
    {
        using MemoryStream memoryStream = new();

        message.WriteTo(memoryStream);

        return Convert.ToBase64String(memoryStream.ToArray());
    }

    public static MimeKit.MimeMessage GetMimeMessageFromBase64(this string base64)
        => MimeKit.MimeMessage.Load(new System.IO.MemoryStream(Convert.FromBase64String(base64)));
}
