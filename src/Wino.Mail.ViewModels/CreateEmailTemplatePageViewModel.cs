using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class CreateEmailTemplatePageViewModel(
    IMessenger messenger,
    IEmailTemplateService emailTemplateService,
    IMailDialogService dialogService,
    INavigationService navigationService,
    ISignatureService signatureService,
    IAccountService accountService,
    IRecipientSuggestionService recipientSuggestionService) : MailBaseViewModel(messenger)
{
    private readonly IEmailTemplateService _emailTemplateService = emailTemplateService;
    private readonly IMailDialogService _dialogService = dialogService;
    private readonly ISignatureService _signatureService = signatureService;
    private readonly IAccountService _accountService = accountService;
    private readonly IRecipientSuggestionService _recipientSuggestionService = recipientSuggestionService;

    private EmailTemplate _editingTemplate;
    private Guid? _suggestionAccountId;

    public INavigationService NavigationService { get; } = navigationService;

    public IRecipientSuggestionService RecipientSuggestionService => _recipientSuggestionService;

    /// <summary>Account used for recipient suggestions when any mailbox exists; otherwise null.</summary>
    public Guid? SuggestionAccountId => _suggestionAccountId;

    [ObservableProperty]
    public partial string TemplateName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TemplateDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TemplateSubject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExistingTemplate { get; set; }

    [ObservableProperty]
    public partial EmailTemplateSignaturePickerItem? SelectedSignatureItem { get; set; }

    public List<EmailTemplateSignaturePickerItem> SignaturePickerItems { get; private set; } = [];

    public ObservableCollection<AccountContact> TemplateToItems { get; } = [];

    public async Task<string> LoadAsync(object parameter)
    {
        EmailTemplate template = null;

        var templateId = parameter switch
        {
            Guid guid when guid != Guid.Empty => guid,
            string value when Guid.TryParse(value, out var parsedGuid) => parsedGuid,
            EmailTemplate emailTemplate when emailTemplate.Id != Guid.Empty => emailTemplate.Id,
            _ => Guid.Empty
        };

        if (templateId != Guid.Empty)
        {
            template = await _emailTemplateService.GetEmailTemplateAsync(templateId).ConfigureAwait(false);
        }

        _editingTemplate = template;

        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        _suggestionAccountId = accounts
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefault();

        var pickerItems = await BuildSignaturePickerItemsAsync(accounts).ConfigureAwait(false);
        var selected = pickerItems.FirstOrDefault(item => item.SignatureId == template?.SignatureId)
            ?? pickerItems[0];
        var toItems = ParseRecipients(template?.ToRecipients);

        await ExecuteUIThread(() =>
        {
            IsExistingTemplate = template != null;
            TemplateName = template?.Name ?? string.Empty;
            TemplateDescription = template?.Description ?? string.Empty;
            TemplateSubject = template?.Subject ?? string.Empty;
            TemplateToItems.Clear();
            foreach (var item in toItems)
                TemplateToItems.Add(item);
            SignaturePickerItems = pickerItems;
            OnPropertyChanged(nameof(SignaturePickerItems));
            SelectedSignatureItem = selected;
        });

        return template?.HtmlContent ?? string.Empty;
    }

    public bool LastSaveSucceeded { get; private set; }
    public bool LastDeleteSucceeded { get; private set; }

    public async Task SaveAsync(string htmlContent)
    {
        LastSaveSucceeded = false;
        var trimmedName = TemplateName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.SettingsEmailTemplates_NameRequired,
                InfoBarMessageType.Warning);
            return;
        }

        var template = _editingTemplate ?? new EmailTemplate
        {
            Id = Guid.NewGuid()
        };

        template.Name = trimmedName;
        template.Description = TemplateDescription?.Trim() ?? string.Empty;
        template.Subject = TemplateSubject?.Trim() ?? string.Empty;
        template.ToRecipients = SerializeRecipients(TemplateToItems);
        template.SignatureId = SelectedSignatureItem?.IsNone == false ? SelectedSignatureItem.SignatureId : null;
        template.HtmlContent = htmlContent ?? string.Empty;

        if (_editingTemplate == null)
        {
            await _emailTemplateService.CreateEmailTemplateAsync(template).ConfigureAwait(false);
        }
        else
        {
            await _emailTemplateService.UpdateEmailTemplateAsync(template).ConfigureAwait(false);
        }

        _editingTemplate = template;
        LastSaveSucceeded = true;
        await ExecuteUIThread(() => NavigationService.GoBack()).ConfigureAwait(false);
    }

    public async Task DeleteAsync()
    {
        LastDeleteSucceeded = false;
        if (_editingTemplate == null)
            return;

        var shouldDelete = await ExecuteUIThreadAsync(() =>
            _dialogService.ShowConfirmationDialogAsync(
                string.Format(Translator.DialogMessage_DeleteEmailTemplateConfirmationMessage, _editingTemplate.Name),
                Translator.DialogMessage_DeleteEmailTemplateConfirmationTitle,
                Translator.Buttons_Delete)).ConfigureAwait(false);

        if (!shouldDelete)
            return;

        await _emailTemplateService.DeleteEmailTemplateAsync(_editingTemplate).ConfigureAwait(false);
        LastDeleteSucceeded = true;
        await ExecuteUIThread(() => NavigationService.GoBack()).ConfigureAwait(false);
    }

    public bool TryAddRecipient(AccountContact recipient)
    {
        if (recipient is null || string.IsNullOrWhiteSpace(recipient.Address)
            || ComposePageViewModel.ContainsAddress(TemplateToItems, recipient.Address))
            return false;

        TemplateToItems.Add(recipient);
        return true;
    }

    public void NotifyAddressExists()
        => _dialogService.InfoBarMessage(Translator.Info_ContactExistsTitle, Translator.Info_ContactExistsMessage, InfoBarMessageType.Warning);

    public void NotifyInvalidEmail(string address)
        => _dialogService.InfoBarMessage(
            Translator.Info_InvalidAddressTitle,
            string.Format(Translator.Info_InvalidAddressMessage, address),
            InfoBarMessageType.Warning);

    public static List<AccountContact> ParseRecipients(string? toRecipients)
    {
        var result = new List<AccountContact>();
        if (string.IsNullOrWhiteSpace(toRecipients))
            return result;

        foreach (var token in toRecipients.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ComposeRecipientRules.TryParseTyped(token, out var address, out var name)
                || !MailAccountAddressValidator.IsValid(address))
                continue;

            if (result.Any(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(new AccountContact
            {
                Address = address,
                Name = name ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(name) ? address : name
            });
        }

        return result;
    }

    public static string SerializeRecipients(IEnumerable<AccountContact> recipients)
    {
        if (recipients is null)
            return string.Empty;

        return string.Join("; ", recipients
            .Where(item => !string.IsNullOrWhiteSpace(item.Address))
            .Select(item =>
            {
                var address = item.Address.Trim();
                var name = string.IsNullOrWhiteSpace(item.Name) ? item.DisplayName : item.Name;
                return string.IsNullOrWhiteSpace(name) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)
                    ? address
                    : $"{name} <{address}>";
            }));
    }

    private async Task<List<EmailTemplateSignaturePickerItem>> BuildSignaturePickerItemsAsync(IReadOnlyList<MailAccount> accounts)
    {
        var items = new List<EmailTemplateSignaturePickerItem>
        {
            new(Translator.SettingsEmailTemplates_SignatureNone)
        };

        foreach (var account in accounts.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var signatures = await _signatureService.GetSignaturesAsync(account.Id).ConfigureAwait(false);
            foreach (var signature in signatures.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var accountLabel = string.IsNullOrWhiteSpace(account.Name) ? account.DisplayAddress : account.Name;
                var signatureLabel = string.IsNullOrWhiteSpace(signature.Name) ? Translator.SettingsSignature_NoneSignatureName : signature.Name;
                items.Add(new EmailTemplateSignaturePickerItem($"{accountLabel} — {signatureLabel}", signature));
            }
        }

        return items;
    }
}
