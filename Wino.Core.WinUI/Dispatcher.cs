using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP;

public class WinUIDispatcher : IDispatcher
{
    private readonly DispatcherQueue _coreDispatcher;

    public WinUIDispatcher(DispatcherQueue coreDispatcher)
    {
        _coreDispatcher = coreDispatcher;
    }

    public Task ExecuteOnUIThread(Action action) => _coreDispatcher.EnqueueAsync(action, DispatcherQueuePriority.Normal);
}
