using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Threading;

public class GmailThreadingStrategy : APIThreadingStrategy, IGmailThreadingStrategy
{
    public GmailThreadingStrategy(IDatabaseService databaseService, IFolderService folderService) : base(databaseService, folderService) { }
}
