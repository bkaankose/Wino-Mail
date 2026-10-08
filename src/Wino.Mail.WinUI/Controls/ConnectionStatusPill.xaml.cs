using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Mail.ViewModels.Data;

namespace Wino.Controls;

/// <summary>
/// The result of a server connection test: not tested, testing, connected, or failed.
/// </summary>
public sealed partial class ConnectionStatusPill : UserControl
{
    [GeneratedDependencyProperty]
    public partial ConnectionTestState State { get; set; }

    public ConnectionStatusPill()
    {
        InitializeComponent();
        UpdateAutomationName(State);
    }

    public Visibility GetStateVisibility(ConnectionTestState state, int visibleState)
        => (int)state == visibleState ? Visibility.Visible : Visibility.Collapsed;

    public bool IsTesting(ConnectionTestState state) => state == ConnectionTestState.Testing;

    partial void OnStateChanged(ConnectionTestState newValue) => UpdateAutomationName(newValue);

    private void UpdateAutomationName(ConnectionTestState state)
        => AutomationProperties.SetName(this, state switch
        {
            ConnectionTestState.Testing => Translator.ImapSetup_StatusTesting,
            ConnectionTestState.Succeeded => Translator.ImapSetup_StatusConnected,
            ConnectionTestState.Failed => Translator.ImapSetup_StatusFailed,
            _ => Translator.ImapSetup_StatusNotTested
        });
}
