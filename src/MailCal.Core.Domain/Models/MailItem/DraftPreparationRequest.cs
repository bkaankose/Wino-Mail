using System;
using System.Text.Json.Serialization;
using MimeKit;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Extensions;

namespace MailCal.Core.Domain.Models.MailItem;

public class DraftPreparationRequest
{
    public DraftPreparationRequest(MailAccount account,
                                   MailCopy createdLocalDraftCopy,
                                   string base64EncodedMimeMessage,
                                   DraftCreationReason reason,
                                   MailCopy referenceMailCopy = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));

        CreatedLocalDraftCopy = createdLocalDraftCopy ?? throw new ArgumentNullException(nameof(createdLocalDraftCopy));
        ReferenceMailCopy = referenceMailCopy;

        // MimeMessage is not serializable with System.Text.Json. Convert to base64 string.
        // This is additional work when deserialization needed, but not much to do atm.

        Base64LocalDraftMimeMessage = base64EncodedMimeMessage;
        Reason = reason;
    }

    [JsonConstructor]
    private DraftPreparationRequest() { }

    public MailCopy CreatedLocalDraftCopy { get; set; }

    public MailCopy ReferenceMailCopy { get; set; }

    public string Base64LocalDraftMimeMessage { get; set; }
    public DraftCreationReason Reason { get; set; }

    [JsonIgnore]
    private MimeMessage createdLocalDraftMimeMessage;

    [JsonIgnore]
    public MimeMessage CreatedLocalDraftMimeMessage
    {
        get
        {
            createdLocalDraftMimeMessage ??= Base64LocalDraftMimeMessage.GetMimeMessageFromBase64();

            return createdLocalDraftMimeMessage;
        }
    }

    public MailAccount Account { get; set; }
}
