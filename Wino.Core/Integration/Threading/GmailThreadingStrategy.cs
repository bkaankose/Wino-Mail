using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Integration.Threading
{
    public class GmailThreadingStrategy : APIThreadingStrategy
    {
        public GmailThreadingStrategy(IDatabaseService databaseService, IFolderService folderService) : base(databaseService, folderService) { }
    }
}
