using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Wino.Domain.Interfaces;

namespace Wino.Shared.WinRT
{
    public class WinAppDispatcher : IDispatcher
    {
        private readonly DispatcherQueue _dispatcherQueue;

        public WinAppDispatcher(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
        }

        public Task ExecuteOnUIThread(Action action) => _dispatcherQueue.EnqueueAsync(() => { action(); });
    }
}
