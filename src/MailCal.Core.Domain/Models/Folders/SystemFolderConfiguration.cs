using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Core.Domain.Models.Folders;

public record SystemFolderConfiguration(MailItemFolder SentFolder,
                                        MailItemFolder DraftFolder,
                                        MailItemFolder ArchiveFolder,
                                        MailItemFolder TrashFolder,
                                        MailItemFolder JunkFolder);
