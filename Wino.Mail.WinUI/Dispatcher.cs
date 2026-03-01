using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI;

public class WinUIDispatcher : IDispatcher
{
    private DispatcherQueue? _coreDispatcher;

    public WinUIDispatcher()
    {
    }

    public WinUIDispatcher(DispatcherQueue coreDispatcher)
    {
        _coreDispatcher = coreDispatcher;
    }

    public bool HasThreadAccess => _coreDispatcher?.HasThreadAccess == true;

    public void Initialize(DispatcherQueue coreDispatcher)
    {
        _coreDispatcher ??= coreDispatcher ?? throw new ArgumentNullException(nameof(coreDispatcher));
    }

    public Task ExecuteOnUIThread(Action action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        if (_coreDispatcher == null)
            throw new InvalidOperationException("UI dispatcher is not initialized.");

        if (_coreDispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        return _coreDispatcher.EnqueueAsync(action, DispatcherQueuePriority.Normal);
    }
}
