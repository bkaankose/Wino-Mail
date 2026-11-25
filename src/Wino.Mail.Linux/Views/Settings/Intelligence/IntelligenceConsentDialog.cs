using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>
/// The Windows ConsentPolicyDialog (macOS IntelligenceConsentPolicySheet): title, notice, the policy page, an
/// acknowledgement that enables Accept, and an inline error when acceptance fails. <c>accept</c> returns whether consent
/// was granted; the dialog stays open on failure. The policy is remote content: ephemeral session, no JavaScript, and a
/// link that leaves the policy's host opens in the browser.
/// </summary>
internal static class IntelligenceConsentDialog
{
    public static Task<bool> PresentAsync(Gtk.Window owner, Uri? policy, IExternalLauncher launcher, Func<Task<bool>> accept)
    {
        WebKit.Module.Initialize();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new List<IDisposable>();
        var dialog = Adw.Dialog.New();
        dialog.SetTitle(Translator.WinoAccount_IntelligenceConsentPolicyTitle);
        dialog.SetContentWidth(560);
        var ownerHeight = owner.GetHeight();
        dialog.SetContentHeight(Math.Min(600, Math.Max(420, (ownerHeight > 0 ? ownerHeight : 680) - 40)));

        var notice = Text(Translator.WinoAccount_IntelligenceConsentPolicyNotice, "dim-label");

        using var session = new GObject.Value(WebKit.NetworkSession.NewEphemeral());
        var web = WebKit.WebView.NewWithProperties([new GObject.ConstructArgument("network-session", session)]);
        var settings = web.GetSettings();
        settings.SetEnableJavascript(false);
        settings.SetEnableDeveloperExtras(false);
        settings.SetAllowFileAccessFromFileUrls(false);
        settings.SetJavascriptCanOpenWindowsAutomatically(false);
        web.SetVexpand(true);
        web.SetHexpand(true);
        WinoA11y.Label(web, Translator.WinoAccount_IntelligenceConsentPolicyTitle);
        var frame = Gtk.Frame.New(null);
        frame.SetChild(web);
        frame.SetSizeRequest(-1, 200);
        frame.SetVexpand(true);
        var policyHost = policy?.Host;
        owned.Add(GtkSignals.ConnectReturning<WebKit.WebView, WebKit.WebView.DecidePolicySignalArgs, bool>(
            h => web.OnDecidePolicy += h, h => web.OnDecidePolicy -= h, (sender, args) =>
            {
                if (args.DecisionType is not (WebKit.PolicyDecisionType.NavigationAction or WebKit.PolicyDecisionType.NewWindowAction)) return false;
                if (args.Decision is not WebKit.NavigationPolicyDecision navigation) return false;
                var address = navigation.GetNavigationAction()?.GetRequest()?.GetUri();
                if (!Uri.TryCreate(address, UriKind.Absolute, out var target)) return false;
                if (args.DecisionType == WebKit.PolicyDecisionType.NavigationAction &&
                    string.Equals(target.Host, policyHost, StringComparison.OrdinalIgnoreCase)) return false;
                args.Decision.Ignore();
                if (target.Scheme is "https" or "http" or "mailto") _ = launcher.LaunchUriAsync(target);
                return true;
            }, false));

        var acknowledgement = Adw.SwitchRow.New();
        acknowledgement.SetUseMarkup(false);
        acknowledgement.SetTitle(Translator.Intelligence_PrivacyPolicyAcknowledgement);
        acknowledgement.SetTitleLines(0);
        var list = Gtk.ListBox.New();
        list.AddCssClass("boxed-list");
        list.SetSelectionMode(Gtk.SelectionMode.None);
        list.Append(acknowledgement);

        var error = Text(Translator.Intelligence_ConsentAcceptanceFailed, "error");
        error.SetVisible(false);
        var spinner = Adw.Spinner.New();
        spinner.SetVisible(false);
        var cancel = WinoA11y.Rename(Gtk.Button.NewWithLabel(Translator.Buttons_Cancel), Translator.Buttons_Cancel);
        var acceptButton = WinoA11y.Rename(Gtk.Button.NewWithLabel(Translator.Intelligence_AcceptPrivacyPolicyButton), Translator.Intelligence_AcceptPrivacyPolicyButton);
        acceptButton.AddCssClass("suggested-action");
        acceptButton.SetSensitive(false);
        var busy = false;
        owned.Add(GtkSignals.OnNotify(acknowledgement, "active", () => acceptButton.SetSensitive(!busy && acknowledgement.GetActive())));

        void Finish(bool value)
        {
            if (!completion.TrySetResult(value)) return;
            dialog.ForceClose();
        }

        owned.Add(GtkSignals.OnClicked(cancel, () => Finish(false)));
        owned.Add(GtkSignals.OnClicked(acceptButton, async () =>
        {
            busy = true;
            acceptButton.SetSensitive(false);
            error.SetVisible(false);
            spinner.SetVisible(true);
            bool ok;
            try { ok = await accept(); }
            catch (Exception exception)
            {
                Serilog.Log.Warning(exception, "Accepting the Wino Intelligence privacy policy failed.");
                ok = false;
            }
            busy = false;
            if (completion.Task.IsCompleted) return;
            spinner.SetVisible(false);
            if (ok)
            {
                Finish(true);
                return;
            }
            error.SetVisible(true);
            acceptButton.SetSensitive(acknowledgement.GetActive());
        }));
        owned.Add(GtkSignals.Connect<Adw.Dialog>(h => dialog.OnClosed += h, h => dialog.OnClosed -= h, (_, _) =>
        {
            completion.TrySetResult(false);
            web.StopLoading();
#if DEBUG
            if (ReferenceEquals(Current, dialog))
            {
                Current = null;
                Acknowledgement = null;
            }
#endif
            // Closing can come from inside one of these handlers; release them after it returned.
            MainLoop.Post(() =>
            {
                foreach (var subscription in owned) subscription.Dispose();
                owned.Clear();
            });
        }));

        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.Append(spinner);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        buttons.Append(spacer);
        buttons.Append(cancel);
        buttons.Append(acceptButton);

        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        stack.SetMarginStart(24);
        stack.SetMarginEnd(24);
        stack.SetMarginTop(12);
        stack.SetMarginBottom(20);
        stack.Append(notice);
        stack.Append(frame);
        stack.Append(list);
        stack.Append(error);
        stack.Append(buttons);

        var header = Adw.HeaderBar.New();
        header.SetShowEndTitleButtons(false);
        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.SetContent(stack);
        dialog.SetChild(view);
        dialog.SetDefaultWidget(acceptButton);
        dialog.Present(owner);
        if (policy is not null) web.LoadUri(policy.AbsoluteUri);
#if DEBUG
        Current = dialog;
        Acknowledgement = acknowledgement;
#endif
        return completion.Task;
    }

    private static Gtk.Label Text(string text, string css)
    {
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        label.AddCssClass(css);
        return label;
    }

#if DEBUG
    internal static Adw.Dialog? Current { get; private set; }
    internal static Adw.SwitchRow? Acknowledgement { get; private set; }
#endif
}
