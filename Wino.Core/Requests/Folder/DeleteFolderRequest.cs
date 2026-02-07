using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Folder;

public record DeleteFolderRequest(MailItemFolder Folder) : FolderRequestBase(Folder, FolderSynchronizerOperation.DeleteFolder)
{
    public override void ApplyUIChanges() { }
    public override void RevertUIChanges() { }
}
