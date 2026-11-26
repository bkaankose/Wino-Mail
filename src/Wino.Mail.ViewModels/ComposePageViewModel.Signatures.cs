using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Validation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Signature choice for the draft (the macOS composer menu and the Windows Options tab picker).
/// Signatures belong to the composing account. The default follows draft creation: the new-message
/// signature for a new mail, the following-message one for replies and forwards, nothing while
/// signatures are off.
/// </summary>
public partial class ComposePageViewModel
{
    private readonly ISignatureService _signatureService;

    /// <summary>The signature HTML believed to be in the body, used to find a signature inserted without its wrapper.</summary>
    private string _insertedSignatureHtml;
    private int _signatureGeneration;

    /// <summary>Signatures of <see cref="ComposingAccount"/>.</summary>
    public ObservableCollection<AccountSignature> AvailableSignatures { get; } = [];

    /// <summary>Sentinel that stands for null; it never reaches <see cref="SelectedSignature"/>.</summary>
    public AccountSignature NoSignatureItem { get; } = new() { Id = Guid.Empty, Name = Translator.Composer_NoSignature };

    public ObservableCollection<AccountSignature> SignaturePickerItems { get; } = [];

    /// <summary>The signature in the draft; null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSignaturePickerItem))]
    public partial AccountSignature SelectedSignature { get; set; }

    public AccountSignature SelectedSignaturePickerItem => SelectedSignature ?? NoSignatureItem;

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
                RebuildSignaturePickerItems();
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
                RebuildSignaturePickerItems();
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

                OnPropertyChanged(nameof(SelectedSignaturePickerItem));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load composer signatures.");
        }
    }

    private void RebuildSignaturePickerItems()
    {
        SignaturePickerItems.Clear();
        SignaturePickerItems.Add(NoSignatureItem);
        foreach (var signature in AvailableSignatures)
            SignaturePickerItems.Add(signature);
    }

    /// <summary>
    /// Replaces the body through <paramref name="renderHtmlAsync"/>, applies the bound signature
    /// (or re-applies the current one), then fills subject and To when those fields are still empty.
    /// </summary>
    public async Task ApplyTemplateAsync(EmailTemplate template, Func<string, Task> renderHtmlAsync)
    {
        if (template == null || renderHtmlAsync == null)
            return;

        await renderHtmlAsync(template.HtmlContent ?? string.Empty).ConfigureAwait(false);

        await ExecuteUIThreadAsync(async () =>
        {
            var boundSignature = template.SignatureId is Guid signatureId
                ? AvailableSignatures.FirstOrDefault(item => item.Id == signatureId)
                : null;

            if (boundSignature != null)
                await ApplySignatureAsync(boundSignature);
            else
                await ReapplySignatureAsync();

            ApplyTemplateSubject(template);
            ApplyTemplateToRecipients(template);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills the subject from an applied template only while the draft has none, so a reply, a forward or a subject
    /// the user typed keeps its own.
    /// </summary>
    public void ApplyTemplateSubject(EmailTemplate template)
    {
        if (template == null || string.IsNullOrWhiteSpace(template.Subject) || !string.IsNullOrWhiteSpace(Subject)) return;

        Subject = template.Subject;
    }

    /// <summary>
    /// Fills To from an applied template only while the draft has no recipients, so a reply or addresses the user
    /// typed keep their own.
    /// </summary>
    public void ApplyTemplateToRecipients(EmailTemplate template)
    {
        if (template == null || string.IsNullOrWhiteSpace(template.ToRecipients) || ToItems.Count > 0)
            return;

        foreach (var token in template.ToRecipients.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ComposeRecipientRules.TryParseTyped(token, out var address, out var name)
                || !MailAccountAddressValidator.IsValid(address))
            {
                NotifyInvalidEmail(token);
                continue;
            }

            TryAddRecipient(ToItems, new AccountContact
            {
                Address = address,
                Name = name ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(name) ? address : name
            });
        }
    }

    /// <summary>Reloads <see cref="AvailableEmailTemplates"/>, for example before showing the picker.</summary>
    public Task RefreshEmailTemplatesAsync() => LoadEmailTemplatesAsync();

    /// <summary>Swaps the signature in the body; null or <see cref="NoSignatureItem"/> removes it.</summary>
    [RelayCommand]
    private async Task ApplySignatureAsync(AccountSignature signature)
    {
        if (ApplySignatureHtmlFunc == null) return;
        if (ReferenceEquals(signature, NoSignatureItem)) signature = null;

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
