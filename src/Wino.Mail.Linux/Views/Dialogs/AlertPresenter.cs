using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

internal sealed record AlertResponse(string Id, string Label, Adw.ResponseAppearance Appearance = Adw.ResponseAppearance.Default, bool Enabled = true);

/// <summary>
/// One alert. Responses show in array order and libadwaita puts the last one at the trailing edge, so callers list
/// cancel first and the primary response last. <see cref="CloseResponse"/> answers Escape and a closing owner; it need
/// not be one of the responses.
/// </summary>
internal sealed record AlertRequest(string Heading, string? Body, AlertResponse[] Responses)
{
    public string CloseResponse { get; init; } = AlertPresenter.Closed;
    public string? DefaultResponse { get; init; }
    public Gtk.Widget? ExtraChild { get; init; }
    public Gtk.Widget? InitialFocus { get; init; }
    public bool PreferWideLayout { get; init; }

    /// <summary>Runs on the built dialog before it is presented (a response that a check box enables).</summary>
    public Action<Adw.AlertDialog>? Configured { get; init; }
}

internal static class AlertPresenter
{
    public const string Closed = "close";

    /// <summary>Main thread only. Completes with the chosen response id, or the request's close response.</summary>
    public static Task<string> PresentAsync(Gtk.Widget owner, AlertRequest request)
    {
        var dialog = Adw.AlertDialog.New(request.Heading, request.Body ?? string.Empty);
        dialog.SetHeadingUseMarkup(false);
        dialog.SetBodyUseMarkup(false);
        var responses = request.Responses.ToArray();
        var labels = Mnemonics.Assign([.. responses.Select(static response => response.Label)]);
        for (var index = 0; index < responses.Length; index++)
        {
            var response = responses[index];
            dialog.AddResponse(response.Id, labels[index]);
            dialog.SetResponseAppearance(response.Id, response.Appearance);
            dialog.SetResponseEnabled(response.Id, response.Enabled);
        }
        dialog.SetCloseResponse(request.CloseResponse);
        if (request.DefaultResponse is { } defaultResponse) dialog.SetDefaultResponse(defaultResponse);
        if (request.ExtraChild is { } extra) dialog.SetExtraChild(extra);
        dialog.SetPreferWideLayout(request.PreferWideLayout);
        request.Configured?.Invoke(dialog);

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // libadwaita emits "response" exactly once per presentation, also for Escape and for an owner that closes,
        // so the response alone ends the alert and no "closed" handler is needed.
        IDisposable? subscription = null;
        subscription = GtkSignals.Connect<Adw.AlertDialog, Adw.AlertDialog.ResponseSignalArgs>(
            h => dialog.OnResponse += h, h => dialog.OnResponse -= h, (_, args) =>
            {
                subscription?.Dispose();
                completion.TrySetResult(args.Response);
            });
        dialog.Present(owner);
        if (request.InitialFocus is { } focus) dialog.SetFocus(focus);
        return completion.Task;
    }
}
