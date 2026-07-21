using System;
using System.Text.Json.Serialization;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.MailItem;

public class DraftPreparationRequest
{
    public DraftPreparationRequest(MailAccount account,
                                   MailCopy createdLocalDraftCopy,
                                   string mimeFilePath,
                                   DraftCreationReason reason,
                                   MailCopy referenceMailCopy = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));

        CreatedLocalDraftCopy = createdLocalDraftCopy ?? throw new ArgumentNullException(nameof(createdLocalDraftCopy));
        ReferenceMailCopy = referenceMailCopy;

        MimeFilePath = string.IsNullOrWhiteSpace(mimeFilePath)
            ? throw new ArgumentException("A MIME file path is required.", nameof(mimeFilePath))
            : mimeFilePath;
        Reason = reason;
    }

    [JsonConstructor]
    private DraftPreparationRequest() { }

    public MailCopy CreatedLocalDraftCopy { get; set; }

    public MailCopy ReferenceMailCopy { get; set; }

    public string MimeFilePath { get; set; }
    public DraftCreationReason Reason { get; set; }

    [JsonIgnore]
    private MimeMessage createdLocalDraftMimeMessage;

    [JsonIgnore]
    public MimeMessage CreatedLocalDraftMimeMessage
    {
        get
        {
            createdLocalDraftMimeMessage ??= MimeMessage.Load(MimeFilePath);

            return createdLocalDraftMimeMessage;
        }
    }

    public MailAccount Account { get; set; }
}
