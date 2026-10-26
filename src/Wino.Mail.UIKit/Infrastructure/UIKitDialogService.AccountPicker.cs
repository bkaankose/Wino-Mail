using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>The account choice behind New mail when several accounts qualify (Windows AccountSelectorFlyout).</summary>
public sealed partial class UIKitDialogService
{
    public async Task<MailAccount?> PickAccountAsync(IReadOnlyList<MailAccount> accounts, string title)
    {
        if (accounts.Count <= 1) return accounts.Count == 1 ? accounts[0] : null;

        var index = await PresentAsync(presenter =>
        {
            var sheet = UIAlertController.Create(title, null, UIAlertControllerStyle.ActionSheet);
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            for (int i = 0; i < accounts.Count; i++)
            {
                int chosen = i;
                sheet.AddAction(UIAlertAction.Create(AccountLabel(accounts[i]), UIAlertActionStyle.Default, _ => completion.TrySetResult(chosen)));
            }
            sheet.AddAction(UIAlertAction.Create(Translator.Buttons_Cancel, UIAlertActionStyle.Cancel, _ => completion.TrySetResult(-1)));
            // The request carries no source control, so on iPad the sheet becomes an arrowless popover in the middle of the presenter.
            if (sheet.PopoverPresentationController is { } popover && presenter.View is { } view)
            {
                popover.SourceView = view;
                popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 0, 0);
                popover.PermittedArrowDirections = (UIPopoverArrowDirection)0;
            }
            presenter.PresentViewController(sheet, true, null);
            return completion.Task;
        }, -1);

        return index >= 0 && index < accounts.Count ? accounts[index] : null;
    }

    private static string AccountLabel(MailAccount account)
    {
        var name = account.Name?.Trim();
        var address = account.Address?.Trim();
        if (string.IsNullOrEmpty(name)) return address ?? string.Empty;
        if (string.IsNullOrEmpty(address) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} ({address})";
    }
}
