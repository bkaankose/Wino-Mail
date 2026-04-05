using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels;

public partial class CreateEmailTemplatePageViewModel(
    IEmailTemplateService emailTemplateService,
    IMailDialogService dialogService,
    INavigationService navigationService) : MailBaseViewModel
{
    private readonly IEmailTemplateService _emailTemplateService = emailTemplateService;
    private readonly IMailDialogService _dialogService = dialogService;

    private EmailTemplate _editingTemplate;

    public INavigationService NavigationService { get; } = navigationService;

    [ObservableProperty]
    public partial string TemplateName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TemplateDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExistingTemplate { get; set; }

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

        await ExecuteUIThread(() =>
        {
            IsExistingTemplate = template != null;
            TemplateName = template?.Name ?? string.Empty;
            TemplateDescription = template?.Description ?? string.Empty;
        });

        return template?.HtmlContent ?? string.Empty;
    }

    public async Task SaveAsync(string htmlContent)
    {
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
        NavigationService.GoBack();
    }

    public async Task DeleteAsync()
    {
        if (_editingTemplate == null)
            return;

        var shouldDelete = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.DialogMessage_DeleteEmailTemplateConfirmationMessage, _editingTemplate.Name),
            Translator.DialogMessage_DeleteEmailTemplateConfirmationTitle,
            Translator.Buttons_Delete).ConfigureAwait(false);

        if (!shouldDelete)
            return;

        await _emailTemplateService.DeleteEmailTemplateAsync(_editingTemplate).ConfigureAwait(false);
        NavigationService.GoBack();
    }
}
