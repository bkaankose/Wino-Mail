using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Views;

namespace Wino.Selectors;

public partial class AccountSetupStepTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AddressTemplate { get; set; }
    public DataTemplate? DiscoveryTemplate { get; set; }
    public DataTemplate? MailTemplate { get; set; }
    public DataTemplate? CalendarTemplate { get; set; }
    public DataTemplate? ContactsTemplate { get; set; }
    public DataTemplate? TasksTemplate { get; set; }
    public DataTemplate? SignInMethodTemplate { get; set; }
    public DataTemplate? SignInTemplate { get; set; }
    public DataTemplate? ProgressTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);

    protected override DataTemplate SelectTemplateCore(object item)
    {
        var step = item is AccountSetupStepContent content ? content.Step : AccountSetupStep.Address;

        var template = step switch
        {
            AccountSetupStep.Discovery => DiscoveryTemplate,
            AccountSetupStep.Mail => MailTemplate,
            AccountSetupStep.Calendar => CalendarTemplate,
            AccountSetupStep.Contacts => ContactsTemplate,
            AccountSetupStep.Tasks => TasksTemplate,
            AccountSetupStep.SignInMethod => SignInMethodTemplate,
            AccountSetupStep.SignIn => SignInTemplate,
            AccountSetupStep.Progress => ProgressTemplate,
            _ => AddressTemplate
        };

        return template ?? throw new InvalidOperationException($"No template for the {step} step.");
    }
}
