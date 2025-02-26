using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Collections;

internal class ThreadingManager
{
    private readonly IThreadingStrategyProvider _threadingStrategyProvider;

    public ThreadingManager(IThreadingStrategyProvider threadingStrategyProvider)
    {
        _threadingStrategyProvider = threadingStrategyProvider;
    }

    public bool ShouldThread(MailCopy newItem, IMailItem existingItem)
    {
        if (_threadingStrategyProvider == null) return false;

        var strategy = _threadingStrategyProvider.GetStrategy(newItem.AssignedAccount.ProviderType);
        return strategy?.ShouldThreadWithItem(newItem, existingItem) ?? false;
    }

    public ThreadMailItem CreateNewThread(IMailItem existingItem, MailCopy newItem)
    {
        var thread = new ThreadMailItem();
        thread.AddThreadItem(existingItem);
        thread.AddThreadItem(newItem);
        return thread;
    }
}
