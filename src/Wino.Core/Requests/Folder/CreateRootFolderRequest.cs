using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Folder;

public record CreateRootFolderRequest(MailItemFolder Folder, string NewFolderName) : FolderRequestBase(Folder, FolderSynchronizerOperation.CreateRootFolder)
{
    public override void ApplyUIChanges() { }
    public override void RevertUIChanges() { }
}
