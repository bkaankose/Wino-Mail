using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Folder;

public record CreateSubFolderRequest(MailItemFolder Folder, string NewFolderName) : FolderRequestBase(Folder, FolderSynchronizerOperation.CreateSubFolder)
{
    public override void ApplyUIChanges() { }
    public override void RevertUIChanges() { }
}
