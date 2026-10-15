using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class EmailTemplatesPageViewModel(IEmailTemplateService emailTemplateService) : MailBaseViewModel
{
    private readonly IEmailTemplateService _emailTemplateService = emailTemplateService;

    public ObservableCollection<EmailTemplate> EmailTemplates { get; } = [];

    public async Task LoadAsync()
    {
        var templates = await _emailTemplateService.GetEmailTemplatesAsync().ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            EmailTemplates.Clear();

            foreach (var template in templates)
            {
                EmailTemplates.Add(template);
            }
        });
    }

    /// <summary>
    /// A list row's Delete: the same confirmation as the Windows editor's Delete, then the list reloads. True when the
    /// template was deleted.
    /// </summary>
    public async Task<bool> DeleteTemplateAsync(EmailTemplate template, IDialogServiceBase dialogService)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        if (template == null) return false;
        var confirmed = await dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.DialogMessage_DeleteEmailTemplateConfirmationMessage, template.Name),
            Translator.DialogMessage_DeleteEmailTemplateConfirmationTitle,
            Translator.Buttons_Delete);
        if (!confirmed) return false;
        await _emailTemplateService.DeleteEmailTemplateAsync(template);
        await LoadAsync();
        return true;
    }

    public void CreateTemplate()
    {
        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.SettingsEmailTemplates_CreatePageTitle,
            WinoPage.CreateEmailTemplatePage));
    }

    public void OpenTemplate(EmailTemplate template)
    {
        if (template == null)
            return;

        var title = string.IsNullOrWhiteSpace(template.Name)
            ? Translator.SettingsEmailTemplates_EditPageTitle
            : template.Name;

        Messenger.Send(new BreadcrumbNavigationRequested(
            title,
            WinoPage.CreateEmailTemplatePage,
            template.Id));
    }
}
