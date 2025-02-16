using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Threading
{
    // Outlook and Gmail is using the same threading strategy.
    // Outlook: ConversationId -> it's set as ThreadId
    // Gmail: ThreadId

    public class OutlookThreadingStrategy : APIThreadingStrategy, IOutlookThreadingStrategy
    {
        public OutlookThreadingStrategy(IDatabaseService databaseService, IFolderService folderService) : base(databaseService, folderService) { }
    }
}
