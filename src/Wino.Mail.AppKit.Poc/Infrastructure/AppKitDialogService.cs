using AppKit;
using Wino.Mail.AppKit.Poc.ViewModels;

namespace Wino.Mail.AppKit.Poc.Infrastructure;

internal sealed class AppKitDialogService : IPocDialogService
{
    public Task ShowMessageAsync(string title, string message)
    {
        var alert = new NSAlert
        {
            AlertStyle = NSAlertStyle.Informational,
            MessageText = title,
            InformativeText = message
        };
        alert.AddButton("OK");
        alert.RunModal();
        return Task.CompletedTask;
    }
}
