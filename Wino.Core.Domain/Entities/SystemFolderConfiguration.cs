using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Entities
{
    public record SystemFolderConfiguration(MailItemFolder SentFolder,
                                            MailItemFolder DraftFolder,
                                            MailItemFolder ArchiveFolder,
                                            MailItemFolder TrashFolder,
                                            MailItemFolder JunkFolder);
}
