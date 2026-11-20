using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Mail.Linux.Infrastructure;

// The dialogs that have a real screen. The dismissed ones are in GtkDialogService.Dismissed.cs.
internal sealed partial class GtkDialogService
{
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType)
        => _ = dispatcher.ExecuteOnUIThread(() => window()?.ShowToast(title, message, null, null));

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action)
        => _ = dispatcher.ExecuteOnUIThread(() => window()?.ShowToast(title, message, actionButtonText, action));

    public Task<bool> ShowServerCertificateTrustDialogAsync(ServerCertificateTrustPrompt prompt)
    {
        var body = new List<string> { prompt.Intro };
        if (prompt.Problems.Length > 0)
            body.Add(prompt.ProblemsHeader + "\n" + string.Join("\n", prompt.Problems.Select(problem => "• " + problem)));
        if (prompt.Facts.Length > 0)
            body.Add(string.Join("\n", prompt.Facts.Select(fact => $"{fact.Label}: {fact.Value}")));
        body.Add(prompt.Closing);
        if (!string.IsNullOrEmpty(prompt.AcknowledgementText)) body.Add(prompt.AcknowledgementText);
        return AskAsync(prompt.Title, string.Join("\n\n", body.Where(text => !string.IsNullOrWhiteSpace(text))), prompt.AcceptButtonText, destructive: prompt.IsDangerous);
    }

    public Task<bool> ShowOpenLinkConfirmationAsync(string? linkText, Uri originalUri, Uri cleanedUri, int removedParameterCount, string? browserName)
        => AskAsync(Translator.OpenLinkDialog_Title, cleanedUri.AbsoluteUri, Translator.OpenLinkDialog_OpenLink, destructive: false);

    private Task<bool> AskAsync(string title, string body, string acceptText, bool destructive)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.ExecuteOnUIThread(() =>
        {
            if (window() is not { } owner)
            {
                completion.TrySetResult(false);
                return;
            }

            var dialog = Adw.AlertDialog.New(title, body);
            dialog.AddResponse("cancel", Translator.Buttons_Cancel);
            dialog.AddResponse("accept", acceptText);
            dialog.SetCloseResponse("cancel");
            dialog.SetDefaultResponse("cancel");
            dialog.SetResponseAppearance("accept", destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested);
            dialog.OnResponse += (_, args) => completion.TrySetResult(args.Response == "accept");
            dialog.Present(owner.Window);
        });
        return completion.Task;
    }
}
