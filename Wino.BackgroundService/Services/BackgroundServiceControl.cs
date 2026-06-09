using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.BackgroundService.Services;

/// <summary>
/// RPC control surface implementation: graceful shutdown requests, toast quick action
/// forwarding from secondary instances, and version reporting.
/// </summary>
public sealed class BackgroundServiceControl : IBackgroundServiceControl
{
    private readonly ToastActionHandler _toastActionHandler;
    private readonly INativeAppService _nativeAppService;
    private readonly Action _terminateAction;

    public BackgroundServiceControl(ToastActionHandler toastActionHandler,
                                    INativeAppService nativeAppService,
                                    Action terminateAction)
    {
        _toastActionHandler = toastActionHandler;
        _nativeAppService = nativeAppService;
        _terminateAction = terminateAction;
    }

    public Task TerminateAsync()
    {
        // Let the RPC response flush before the process exits.
        _ = Task.Delay(TimeSpan.FromMilliseconds(250)).ContinueWith(_ => _terminateAction());
        return Task.CompletedTask;
    }

    public Task HandleToastActionsAsync(string toastArguments)
        => _toastActionHandler.HandleAsync(toastArguments);

    public Task<string> GetServerVersionAsync()
        => Task.FromResult(_nativeAppService.GetFullAppVersion());
}
