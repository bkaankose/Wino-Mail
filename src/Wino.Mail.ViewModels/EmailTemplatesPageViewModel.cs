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
