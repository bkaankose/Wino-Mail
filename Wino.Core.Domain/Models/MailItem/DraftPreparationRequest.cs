using System;
using System.Text.Json.Serialization;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.MailItem;

public class DraftPreparationRequest
{
    public DraftPreparationRequest(MailAccount account, MailCopy createdLocalDraftCopy, string base64EncodedMimeMessage, MailCopy referenceMailCopy = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));

        CreatedLocalDraftCopy = createdLocalDraftCopy ?? throw new ArgumentNullException(nameof(createdLocalDraftCopy));
        ReferenceMailCopy = referenceMailCopy;

        // MimeMessage is not serializable with System.Text.Json. Convert to base64 string.
        // This is additional work when deserialization needed, but not much to do atm.

        Base64LocalDraftMimeMessage = base64EncodedMimeMessage;
    }

    [JsonConstructor]
    private DraftPreparationRequest() { }

    public MailCopy CreatedLocalDraftCopy { get; set; }

    public MailCopy ReferenceMailCopy { get; set; }

    public string Base64LocalDraftMimeMessage { get; set; }

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

    public MailAccount Account { get; }
}
