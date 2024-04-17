using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Integration.Threading
{
    // Outlook and Gmail is using the same threading strategy.
    // Outlook: ConversationId -> it's set as ThreadId
    // Gmail: ThreadId

    public class OutlookThreadingStrategy : APIThreadingStrategy
    {
        public OutlookThreadingStrategy(IDatabaseService databaseService, IFolderService folderService) : base(databaseService, folderService) { }
    }
}
