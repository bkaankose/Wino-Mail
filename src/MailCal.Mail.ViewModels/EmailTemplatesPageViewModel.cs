using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Messaging.Client.Navigation;

namespace MailCal.Mail.ViewModels;

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

    public void CreateTemplate()
    {
        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.SettingsEmailTemplates_CreatePageTitle,
            MailCalPage.CreateEmailTemplatePage));
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
            MailCalPage.CreateEmailTemplatePage,
            template.Id));
    }
}
