using System.Linq;
using MimeKit;
using Wino.Renderer.Extraction;
using Wino.Renderer.Mime;
using Wino.Services.Interfaces;
using DomainUnsubscribeInfo = Wino.Core.Domain.Models.Reader.UnsubscribeInfo;
using DomainUnsubscribeMethod = Wino.Core.Domain.Enums.UnsubscribeMethod;
using DomainUnsubscribeOption = Wino.Core.Domain.Models.Reader.UnsubscribeOption;
using DomainUnsubscribeSource = Wino.Core.Domain.Enums.UnsubscribeSource;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// Unsubscribe information from the mail engine, for code that has a message but no reader (the assistant).
/// </summary>
public sealed class EngineUnsubscribeReader : IMailUnsubscribeReader
{
    public DomainUnsubscribeInfo? Read(MimeMessage message)
    {
        if (message is null)
            return null;

        using var content = MimeMailContent.Create(message);

        return ToDomain(content.Analysis.Unsubscribe);
    }

    public static DomainUnsubscribeInfo? ToDomain(MailUnsubscribe? unsubscribe)
    {
        if (unsubscribe is null || unsubscribe.Methods.Count == 0)
            return null;

        var info = new DomainUnsubscribeInfo
        {
            Source = unsubscribe.Source == UnsubscribeSource.Body ? DomainUnsubscribeSource.Body : DomainUnsubscribeSource.Header,
            IsOneClick = unsubscribe.OneClickAllowed,
            OneClickUrl = unsubscribe.OneClickUrl,
            OneClickHeadersSigned = unsubscribe.OneClickSignature == UnsubscribeSignature.Covered,
            HttpLink = unsubscribe.WebUrl,
            MailToLink = unsubscribe.MailtoUri,
            BodyLinkText = unsubscribe.BodyLinkText,
            ListId = unsubscribe.ListId
        };

        foreach (var target in unsubscribe.Methods)
        {
            info.Methods.Add(new DomainUnsubscribeOption
            {
                Method = target.Method switch
                {
                    UnsubscribeMethod.OneClick => DomainUnsubscribeMethod.OneClick,
                    UnsubscribeMethod.Web => DomainUnsubscribeMethod.Website,
                    _ => DomainUnsubscribeMethod.Mail
                },
                Uri = target.Uri,
                MailRecipients = target.Mailto?.To.ToList() ?? [],
                MailSubject = target.Mailto?.Subject,
                MailBody = target.Mailto?.Body
            });
        }

        return info;
    }
}
