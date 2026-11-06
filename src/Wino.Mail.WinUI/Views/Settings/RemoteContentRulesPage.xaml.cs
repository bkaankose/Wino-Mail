using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class RemoteContentRulesPage : RemoteContentRulesPageAbstract
{
    public RemoteContentRulesPage()
    {
        InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }

    private void NewPatternKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.AddRuleCommand.CanExecute(null))
            return;

        e.Handled = true;
        ViewModel.AddRuleCommand.Execute(null);
    }
}
