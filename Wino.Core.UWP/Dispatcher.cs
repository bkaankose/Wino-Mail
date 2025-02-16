using System;
using System.Threading.Tasks;
using Windows.UI.Core;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP;

public class UWPDispatcher : IDispatcher
{
    private readonly CoreDispatcher _coreDispatcher;

    public UWPDispatcher(CoreDispatcher coreDispatcher)
    {
        _coreDispatcher = coreDispatcher;
    }

    public Task ExecuteOnUIThread(Action action)
        => _coreDispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask();
}
