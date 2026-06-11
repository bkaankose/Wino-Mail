using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Send request prepared in the UI. S/MIME protection is described by flags only: the
/// companion applies the actual signing/encryption (see ISmimeService) before queueing,
/// so no cryptography runs in the UI process. <see cref="Base64MimeMessage"/> may be
/// null/empty: the companion then loads the draft MIME from shared storage by
/// MailItem.FileId (the UI saves drafts through IMailRenderService and never serializes
/// MIME itself).
/// </summary>
public record SendDraftPreparationRequest(MailCopy MailItem,
                                          MailAccountAlias SendingAlias,
                                          MailItemFolder SentFolder,
                                          MailItemFolder DraftFolder,
                                          MailAccountPreferences AccountPreferences,
                                          string Base64MimeMessage,
                                          bool SmimeSign = false,
                                          bool SmimeEncrypt = false,
                                          string SmimeSigningCertificateThumbprint = null);
