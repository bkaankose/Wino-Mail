using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Extensions;
using Wino.Services.Extensions;

namespace Wino.Core.Services;

/// <summary>
/// Companion-side MIME rendering and draft editing. The UI talks to this service over
/// RPC and never parses .eml itself; HTML and inline resources land in the shared MIME
/// resource directory, attachments are extracted to files on demand.
/// </summary>
public class MailRenderService : IMailRenderService
{
    private const string MimeFileName = "mail.eml";
    private const string AttachmentsSubFolderName = "attachments";

    private readonly IMimeFileService _mimeFileService;
    private readonly ISmimeService _smimeService;
    private readonly IMailService _mailService;
    private readonly IAccountService _accountService;

    public MailRenderService(IMimeFileService mimeFileService,
                             ISmimeService smimeService,
                             IMailService mailService,
                             IAccountService accountService)
    {
        _mimeFileService = mimeFileService;
        _smimeService = smimeService;
        _mailService = mailService;
        _accountService = accountService;
    }

    public async Task<MailRenderInfo> RenderMailAsync(Guid fileId, Guid accountId, MailRenderingOptions options)
    {
        var (message, resourcePath, smimeInfo) = await LoadMessageForRenderAsync(fileId, accountId).ConfigureAwait(false);

        var renderInfo = BuildRenderInfo(message, resourcePath, options);
        renderInfo.SmimeInfo = smimeInfo;
        renderInfo.EmlFilePath = Path.Combine(resourcePath, MimeFileName);

        return renderInfo;
    }

    public async Task<MailRenderInfo> RenderEmlFileAsync(string emlFilePath, MailRenderingOptions options)
    {
        var message = await MimeMessage.LoadAsync(emlFilePath).ConfigureAwait(false);

        // Inline resources are written next to the eml when possible; fall back to temp
        // for read-only locations.
        var resourcePath = GetEmlResourceDirectory(emlFilePath);

        var renderInfo = BuildRenderInfo(message, resourcePath, options);
        renderInfo.EmlFilePath = emlFilePath;

        return renderInfo;
    }

    public async Task<string> ExtractAttachmentAsync(Guid fileId, Guid accountId, int attachmentIndex)
    {
        var (message, resourcePath, _) = await LoadMessageForRenderAsync(fileId, accountId).ConfigureAwait(false);

        return await ExtractAttachmentInternalAsync(message, resourcePath, attachmentIndex).ConfigureAwait(false);
    }

    public async Task<string> ExtractEmlFileAttachmentAsync(string emlFilePath, int attachmentIndex)
    {
        var message = await MimeMessage.LoadAsync(emlFilePath).ConfigureAwait(false);

        return await ExtractAttachmentInternalAsync(message, GetEmlResourceDirectory(emlFilePath), attachmentIndex).ConfigureAwait(false);
    }

    public async Task<string> GetCalendarInvitationIcsAsync(Guid fileId, Guid accountId)
    {
        var mimeMessageInformation = await _mimeFileService.GetMimeMessageInformationAsync(fileId, accountId).ConfigureAwait(false);

        var message = mimeMessageInformation.MimeMessage;

        var calendarTextPart = message.BodyParts
            .OfType<TextPart>()
            .FirstOrDefault(p => p.ContentType?.IsMimeType("text", "calendar") == true);

        if (calendarTextPart != null)
            return calendarTextPart.Text;

        var calendarMimePart = message.BodyParts
            .OfType<MimePart>()
            .FirstOrDefault(p => p.ContentType?.IsMimeType("text", "calendar") == true);

        if (calendarMimePart == null)
            return null;

        using var stream = new MemoryStream();
        calendarMimePart.Content?.DecodeTo(stream);
        var contentBytes = stream.ToArray();

        if (contentBytes.Length == 0)
            return null;

        var charset = calendarMimePart.ContentType?.Charset;
        var encoding = string.IsNullOrWhiteSpace(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset);
        return encoding.GetString(contentBytes);
    }

    public async Task<MailDraftContent> GetDraftContentAsync(Guid fileId, Guid accountId)
    {
        var mimeMessageInformation = await _mimeFileService.GetMimeMessageInformationAsync(fileId, accountId).ConfigureAwait(false);
        var message = mimeMessageInformation.MimeMessage;

        // The editor is seeded with the rendered body of the existing draft.
        var renderModel = _mimeFileService.GetMailRenderModel(message, mimeMessageInformation.Path);

        var content = new MailDraftContent
        {
            Subject = message.Subject,
            HtmlBody = renderModel.RenderHtml,
            To = MapRecipients(message.To),
            Cc = MapRecipients(message.Cc),
            Bcc = MapRecipients(message.Bcc),
            Importance = MapImportance(message.Importance),
            IsReadReceiptRequested = message.HasReadReceiptRequest(),
            FromName = MailkitMessageExtensions.GetActualSenderName(message),
            FromAddress = message.From.Mailboxes.FirstOrDefault()?.Address,
            ReplyToAddress = message.ReplyTo.Mailboxes.FirstOrDefault()?.Address,
            InReplyTo = message.InReplyTo
        };

        // Existing draft attachments are extracted so the UI can show and re-attach them
        // as plain files.
        for (var i = 0; i < renderModel.Attachments.Count; i++)
        {
            var extractedPath = await ExtractToFileAsync(renderModel.Attachments[i], mimeMessageInformation.Path, i).ConfigureAwait(false);
            content.Attachments.Add(new DraftAttachmentInfo(Path.GetFileName(extractedPath), extractedPath, new FileInfo(extractedPath).Length));
        }

        return content;
    }

    public async Task SaveDraftContentAsync(Guid mailCopyUniqueId, MailDraftContent content)
    {
        var mailCopy = await _mailService.GetSingleMailItemAsync(mailCopyUniqueId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Draft mail {mailCopyUniqueId} was not found.");

        var account = await _accountService.GetAccountAsync(mailCopy.AssignedAccount?.Id ?? mailCopy.AssignedFolder.MailAccountId).ConfigureAwait(false);
        var mimeMessageInformation = await _mimeFileService.GetMimeMessageInformationAsync(mailCopy.FileId, account.Id).ConfigureAwait(false);
        var message = mimeMessageInformation.MimeMessage;

        // Recipients.
        ApplyRecipients(message.To, content.To);
        ApplyRecipients(message.Cc, content.Cc);
        ApplyRecipients(message.Bcc, content.Bcc);

        message.Subject = content.Subject ?? string.Empty;
        message.Importance = MapImportance(content.Importance);

        // Sender identity from the alias selected in the UI.
        if (!string.IsNullOrEmpty(content.FromAddress))
        {
            message.From.Clear();
            message.From.Add(new MailboxAddress(content.FromName ?? content.FromAddress, content.FromAddress));
        }

        message.ReplyTo.Clear();
        if (!string.IsNullOrEmpty(content.ReplyToAddress))
        {
            message.ReplyTo.Add(new MailboxAddress(content.ReplyToAddress, content.ReplyToAddress));
        }

        message.SetReadReceiptRequest(content.FromAddress ?? account.Address ?? string.Empty, content.IsReadReceiptRequested);

        // Body: editor HTML (data images become linked resources) + attachments by file path.
        var bodyBuilder = new BodyBuilder();
        bodyBuilder.SetHtmlBody(content.HtmlBody ?? string.Empty);

        foreach (var attachment in content.Attachments.Where(a => !string.IsNullOrEmpty(a.FilePath) && File.Exists(a.FilePath)))
        {
            await bodyBuilder.Attachments.AddAsync(attachment.FilePath).ConfigureAwait(false);
        }

        message.Body = bodyBuilder.ToMessageBody();

        await _mimeFileService.SaveMimeMessageAsync(mailCopy.FileId, message, account.Id).ConfigureAwait(false);

        // Keep the database copy aligned for list display.
        mailCopy.Subject = message.Subject;
        mailCopy.PreviewText = message.TextBody;
        if (!string.IsNullOrEmpty(content.FromAddress))
            mailCopy.FromAddress = content.FromAddress;
        mailCopy.HasAttachments = content.Attachments.Count > 0;

        await _mailService.UpdateMailAsync(mailCopy).ConfigureAwait(false);
    }

    private async Task<(MimeMessage Message, string ResourcePath, SmimeRenderInfo SmimeInfo)> LoadMessageForRenderAsync(Guid fileId, Guid accountId)
    {
        var mimeMessageInformation = await _mimeFileService.GetMimeMessageInformationAsync(fileId, accountId).ConfigureAwait(false);
        var message = mimeMessageInformation.MimeMessage;
        SmimeRenderInfo smimeInfo = null;

        if (IsSmimeProtected(message))
        {
            smimeInfo = await _smimeService.PrepareSmimeRenderAsync(fileId, accountId).ConfigureAwait(false);

            if (smimeInfo?.ProcessedMimeFileName != null)
            {
                var processedPath = Path.Combine(mimeMessageInformation.Path, smimeInfo.ProcessedMimeFileName);
                message = await MimeMessage.LoadAsync(processedPath).ConfigureAwait(false);
            }
        }

        return (message, mimeMessageInformation.Path, smimeInfo);
    }

    private MailRenderInfo BuildRenderInfo(MimeMessage message, string resourcePath, MailRenderingOptions options)
    {
        var renderModel = _mimeFileService.GetMailRenderModel(message, resourcePath, options);

        var renderInfo = new MailRenderInfo
        {
            RenderHtml = renderModel.RenderHtml,
            AccessibleText = renderModel.AccessibleText,
            Subject = message.Subject,
            CreationDate = message.Date.UtcDateTime,
            FromName = MailkitMessageExtensions.GetActualSenderName(message),
            FromAddress = MailkitMessageExtensions.GetActualSenderAddress(message),
            To = MapRecipients(message.To),
            Cc = MapRecipients(message.Cc),
            Bcc = MapRecipients(message.Bcc),
            UnsubscribeInfo = renderModel.UnsubscribeInfo,
            RenderingOptions = options
        };

        for (var i = 0; i < renderModel.Attachments.Count; i++)
        {
            var part = renderModel.Attachments[i];
            renderInfo.Attachments.Add(new MailAttachmentInfo(i, GetAttachmentFileName(part, i), GetAttachmentSize(part)));
        }

        return renderInfo;
    }

    private async Task<string> ExtractAttachmentInternalAsync(MimeMessage message, string resourcePath, int attachmentIndex)
    {
        // The attachment list of GetMailRenderModel is deterministic for the same
        // message, so the index from the render info stays valid here.
        var renderModel = _mimeFileService.GetMailRenderModel(message, resourcePath);

        if (attachmentIndex < 0 || attachmentIndex >= renderModel.Attachments.Count)
            throw new ArgumentOutOfRangeException(nameof(attachmentIndex), attachmentIndex, "Attachment index is out of range for the message.");

        return await ExtractToFileAsync(renderModel.Attachments[attachmentIndex], resourcePath, attachmentIndex).ConfigureAwait(false);
    }

    private static async Task<string> ExtractToFileAsync(MimePart part, string resourcePath, int attachmentIndex)
    {
        var attachmentsDirectory = Path.Combine(resourcePath, AttachmentsSubFolderName);
        Directory.CreateDirectory(attachmentsDirectory);

        // Index prefix keeps equally named attachments apart.
        var targetPath = Path.Combine(attachmentsDirectory, $"{attachmentIndex}_{SanitizeFileName(GetAttachmentFileName(part, attachmentIndex))}");

        await using (var fileStream = File.Create(targetPath))
        {
            await part.Content.DecodeToAsync(fileStream).ConfigureAwait(false);
        }

        return targetPath;
    }

    private string GetEmlResourceDirectory(string emlFilePath)
    {
        var directory = Path.GetDirectoryName(emlFilePath);

        if (string.IsNullOrEmpty(directory) || !HasWriteAccess(directory))
        {
            directory = Path.Combine(Path.GetTempPath(), "WinoEmlRender", SanitizeFileName(Path.GetFileNameWithoutExtension(emlFilePath)));
            Directory.CreateDirectory(directory);
        }

        return directory;
    }

    private static bool HasWriteAccess(string directory)
    {
        try
        {
            var probePath = Path.Combine(directory, $".wino-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probePath, []);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<MailRecipientInfo> MapRecipients(InternetAddressList addressList)
    {
        var recipients = new List<MailRecipientInfo>();
        Flatten(addressList);
        return recipients;

        void Flatten(InternetAddressList list)
        {
            foreach (var address in list)
            {
                if (address is MailboxAddress mailbox)
                    recipients.Add(new MailRecipientInfo(mailbox.Name, mailbox.Address));
                else if (address is GroupAddress group)
                    Flatten(group.Members);
            }
        }
    }

    private static void ApplyRecipients(InternetAddressList target, List<MailRecipientInfo> recipients)
    {
        target.Clear();

        foreach (var recipient in recipients ?? [])
        {
            target.Add(new MailboxAddress(recipient.Name, recipient.Address));
        }
    }

    private static Domain.Enums.MailImportance MapImportance(MessageImportance importance) => importance switch
    {
        MessageImportance.High => Domain.Enums.MailImportance.High,
        MessageImportance.Low => Domain.Enums.MailImportance.Low,
        _ => Domain.Enums.MailImportance.Normal
    };

    private static MessageImportance MapImportance(Domain.Enums.MailImportance importance) => importance switch
    {
        Domain.Enums.MailImportance.High => MessageImportance.High,
        Domain.Enums.MailImportance.Low => MessageImportance.Low,
        _ => MessageImportance.Normal
    };

    private static string GetAttachmentFileName(MimePart part, int index)
        => string.IsNullOrWhiteSpace(part.FileName) ? $"attachment-{index}" : part.FileName;

    private static long GetAttachmentSize(MimePart part)
    {
        try
        {
            return part.Content?.Stream?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = (value ?? "attachment").Trim().ToCharArray();

        for (var i = 0; i < characters.Length; i++)
        {
            if (Array.IndexOf(invalidCharacters, characters[i]) >= 0)
                characters[i] = '_';
        }

        var sanitized = new string(characters).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
    }

    /// <summary>
    /// Content-type based detection; mirrors the check used before the render RPC existed.
    /// </summary>
    private static bool IsSmimeProtected(MimeMessage message)
    {
        var contentType = message?.Body?.ContentType;

        if (contentType == null)
            return false;

        if (contentType.IsMimeType("application", "pkcs7-mime") || contentType.IsMimeType("application", "x-pkcs7-mime"))
            return true;

        return contentType.IsMimeType("multipart", "signed") &&
               (contentType.Parameters["protocol"]?.Contains("pkcs7", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
