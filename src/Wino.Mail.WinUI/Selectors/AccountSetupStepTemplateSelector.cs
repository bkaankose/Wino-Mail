using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Views.Account;

namespace Wino.Mail.WinUI.Selectors;

public partial class AccountSetupStepTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AddressTemplate { get; set; }
    public DataTemplate? DiscoveryTemplate { get; set; }
    public DataTemplate? SignInMethodTemplate { get; set; }
    public DataTemplate? SignInTemplate { get; set; }
    public DataTemplate? ReviewTemplate { get; set; }
    public DataTemplate? ProgressTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);

    protected override DataTemplate SelectTemplateCore(object item)
    {
        var step = item is AccountSetupStepContent content ? content.Step : AccountSetupStep.Address;

        var template = step switch
        {
            AccountSetupStep.Discovery => DiscoveryTemplate,
            AccountSetupStep.ServerKind or AccountSetupStep.SignInMethod => SignInMethodTemplate,
            AccountSetupStep.SignIn => SignInTemplate,
            AccountSetupStep.Review => ReviewTemplate,
            AccountSetupStep.Progress => ProgressTemplate,
            _ => AddressTemplate
        };

        return template ?? throw new InvalidOperationException($"No template for the {step} step.");
    }
}
