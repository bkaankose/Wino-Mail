using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Signature choice for the draft (macOS composer picker; Windows has none and does not set
/// <see cref="ApplySignatureHtmlFunc"/>). Signatures belong to the composing account. The default
/// follows draft creation: the new-message signature for a new mail, the following-message one for
/// replies and forwards, nothing while signatures are off.
/// </summary>
public partial class ComposePageViewModel
{
    private readonly ISignatureService _signatureService;

    /// <summary>The signature HTML believed to be in the body, used to find a signature inserted without its wrapper.</summary>
    private string _insertedSignatureHtml;
    private int _signatureGeneration;

    /// <summary>Signatures of <see cref="ComposingAccount"/>.</summary>
    public ObservableCollection<AccountSignature> AvailableSignatures { get; } = [];

    /// <summary>The signature in the draft; null for none.</summary>
    [ObservableProperty]
    public partial AccountSignature SelectedSignature { get; set; }

    /// <summary>Editor hook: (new signature HTML or empty to remove, previously inserted HTML or null).</summary>
    public Func<string, string, Task> ApplySignatureHtmlFunc { get; set; }

    /// <summary>When set, rewrite failures go here instead of the info bar (the macOS inline error strip).</summary>
    public Action<string> RewriteErrorHandler { get; set; }

    partial void OnCurrentMimeMessageChanged(MimeMessage value) => _ = LoadSignaturesAsync();

    /// <summary>
    /// Loads the composing account's signatures and selects the one draft creation inserted, or keeps
    /// the current choice when <paramref name="keepSelection"/> is set (a refresh after editing them).
    /// </summary>
    public async Task LoadSignaturesAsync(bool keepSelection = false)
    {
        var generation = ++_signatureGeneration;
        var account = ComposingAccount;
        var mime = CurrentMimeMessage;
        if (_signatureService == null || account == null)
        {
            await ExecuteUIThread(() =>
            {
                if (generation != _signatureGeneration) return;
                AvailableSignatures.Clear();
                SelectedSignature = null;
                _insertedSignatureHtml = null;
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            var signatures = await _signatureService.GetSignaturesAsync(account.Id).ConfigureAwait(false);
            var isFollowing = mime != null && (!string.IsNullOrEmpty(mime.InReplyTo) || mime.References.Count > 0);
            var preferences = account.Preferences;
            var defaultId = preferences?.IsSignatureEnabled == true
                ? (isFollowing ? preferences.SignatureIdForFollowingMessages : preferences.SignatureIdForNewMessages)
                : null;

            await ExecuteUIThread(() =>
            {
                if (generation != _signatureGeneration) return;
                AvailableSignatures.Clear();
                foreach (var signature in signatures.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    AvailableSignatures.Add(signature);
                if (keepSelection)
                {
                    var currentId = SelectedSignature?.Id;
                    SelectedSignature = currentId == null ? null : AvailableSignatures.FirstOrDefault(x => x.Id == currentId);
                }
                else
                {
                    SelectedSignature = AvailableSignatures.FirstOrDefault(x => x.Id == defaultId);
                    _insertedSignatureHtml = SelectedSignature?.HtmlBody;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load composer signatures.");
        }
    }

    /// <summary>Reloads <see cref="AvailableEmailTemplates"/>, for example before showing the picker.</summary>
    public Task RefreshEmailTemplatesAsync() => LoadEmailTemplatesAsync();

    /// <summary>Swaps the signature in the body; null removes it.</summary>
    [RelayCommand]
    private async Task ApplySignatureAsync(AccountSignature signature)
    {
        if (ApplySignatureHtmlFunc == null) return;

        await ApplySignatureHtmlFunc(signature?.HtmlBody ?? string.Empty, _insertedSignatureHtml);
        _insertedSignatureHtml = signature?.HtmlBody;
        SelectedSignature = signature;
    }

    /// <summary>Inserts the selected signature again after the body was replaced, for example by a template.</summary>
    public async Task ReapplySignatureAsync()
    {
        if (ApplySignatureHtmlFunc == null || SelectedSignature?.HtmlBody is not { Length: > 0 } html) return;

        await ApplySignatureHtmlFunc(html, null);
        _insertedSignatureHtml = html;
    }
}
