using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.Folders;

public record SystemFolderConfiguration(MailItemFolder SentFolder,
                                        MailItemFolder DraftFolder,
                                        MailItemFolder ArchiveFolder,
                                        MailItemFolder TrashFolder,
                                        MailItemFolder JunkFolder);
