using System;
using System.IO;
using Wino.Core.Domain.Models.MailItem;

// Lives in Wino.Services (companion-only) because it touches MimeKit;
// the namespace is kept so companion consumers stay unchanged.
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

    /// <summary>
    /// Parses the MIME payload of a send request. Parses on every call — callers that
    /// mutate the returned message (header removal etc.) must hold on to the instance.
    /// </summary>
    public static MimeKit.MimeMessage GetMimeMessage(this SendDraftPreparationRequest request)
        => request.Base64MimeMessage.GetMimeMessageFromBase64();

    /// <summary>
    /// Parses the local draft MIME payload of a draft request. Parses on every call.
    /// </summary>
    public static MimeKit.MimeMessage GetCreatedLocalDraftMimeMessage(this DraftPreparationRequest request)
        => request.Base64LocalDraftMimeMessage.GetMimeMessageFromBase64();
}
