#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// Debug-only bridge commands for the phone Mail flow in the Simulator (the shared <c>seedmail</c> creates the demo
/// account): <c>mailboxes</c> lists the Mailboxes rows; <c>openfolder NAME</c> taps a Mailboxes folder row; <c>pop</c> pops
/// the current stack natively; <c>readertoolbar</c> lists the reader toolbar buttons; <c>readertap N</c> taps one; <c>newmail</c> is the New Message button; <c>moderoot</c> and <c>moderoot-tap N</c> list or tap the active mode's menu root (To Do Lists); <c>stackinfo</c> lists the current stack and its back title. The list page adds its own commands while it is active.
/// </summary>
internal sealed class PhoneMailDebugHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        UIKitDebugBridge.Register("mailboxes", _ => Task.FromResult(Describe()));
        UIKitDebugBridge.Register("openfolder", args => Task.FromResult(OpenFolder(string.Join(' ', args))));
        // A native back (the back button or edge swipe), unlike the bridge's router "back".
        UIKitDebugBridge.Register("toolbarinfo", _ => Task.FromResult(Shell?.CurrentNavigationController?.Toolbar is { } bar ? Dump(bar, 0) : "no toolbar"));
        UIKitDebugBridge.Register("readertoolbar", _ => Task.FromResult(Shell?.DebugReaderToolbar() ?? "no shell"));
        UIKitDebugBridge.Register("readertap", args => Task.FromResult(Shell?.DebugTapReaderButton(args.Length > 0 && int.TryParse(args[0], out var index) ? index : -1) ?? "no shell"));
        UIKitDebugBridge.Register("newmail", _ => { Shell?.Compose(); return Task.FromResult(Shell is null ? "no shell" : "ok"); });
        UIKitDebugBridge.Register("composeprobe", _ => ProbeComposeAsync());
        UIKitDebugBridge.Register("moderoot", _ => Task.FromResult(Shell?.DebugModeRoot(null) ?? "no shell"));
        UIKitDebugBridge.Register("moderoot-tap", args => Task.FromResult(Shell?.DebugModeRoot(args.Length > 0 && int.TryParse(args[0], out var row) ? row : -1) ?? "no shell"));
        UIKitDebugBridge.Register("stackinfo", _ => Task.FromResult(Shell?.CurrentNavigationController is { } stack
            ? string.Join(" > ", (stack.ViewControllers ?? []).Select(page => $"{page.GetType().Name}('{page.NavigationItem.Title}')")) + $" back='{stack.NavigationBar.BackItem?.Title}'"
            : "no stack"));
        UIKitDebugBridge.Register("pop", _ => Task.FromResult(Shell?.CurrentNavigationController?.PopViewController(true) is { } popped ? "ok " + popped.GetType().Name : "nothing to pop"));
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;

    private PhoneShellViewController? Shell => services.GetService<UIKitNavigationService>()?.Shell as PhoneShellViewController;

    private static string Dump(global::UIKit.UIView view, int depth)
    {
        if (depth > 9) return string.Empty;
        var line = $"{new string(' ', depth)}{view.GetType().Name} {view.Frame} hidden={view.Hidden}" + (view is global::UIKit.UIImageView image ? $" image={image.Image?.Size}" : string.Empty);
        return line + "\n" + string.Concat(view.Subviews.Select(child => Dump(child, depth + 1)));
    }

    /// <summary>Runs New Message awaited and reports the draft-creation inputs (selected item, account, Drafts folder).</summary>
    private async Task<string> ProbeComposeAsync()
    {
        var shell = services.GetRequiredService<Wino.Mail.ViewModels.MailAppShellViewModel>();
        var accounts = await services.GetRequiredService<Wino.Core.Domain.Interfaces.IAccountService>().GetAccountsAsync();
        var folders = services.GetRequiredService<Wino.Core.Domain.Interfaces.IFolderService>();
        var report = $"selected={shell.SelectedMenuItem?.GetType().Name ?? "null"} accounts={accounts.Count}";
        foreach (var account in accounts)
        {
            var draft = await folders.GetSpecialFolderByAccountIdAsync(account.Id, Wino.Core.Domain.Enums.SpecialFolderType.Draft);
            report += $" [{account.Address} granted={account.IsMailAccessGranted} drafts={draft?.FolderName ?? "none"}]";
        }
        var run = shell.HandleCreateNewMailAsync();
        bool completed = await Task.WhenAny(run, Task.Delay(8000)) == run;
        return $"{report} completed={completed} error={run.Exception?.GetBaseException().Message ?? "none"}";
    }

    private string Describe() => Shell?.DebugDescribeMailboxes() ?? "no shell";

    private string OpenFolder(string name) => Shell?.DebugOpenFolder(name) ?? "no shell";
}
#endif
