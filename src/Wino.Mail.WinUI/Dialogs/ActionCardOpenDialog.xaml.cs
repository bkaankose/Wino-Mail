using System;
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;

namespace Wino.Mail.WinUI.Dialogs;

/// <summary>
/// Asks before an action card opens an address the sender put in it. The host name stands out, because it is the part
/// that tells the user where they are going.
/// </summary>
public sealed partial class ActionCardOpenDialog : ContentDialog
{
    public ActionCardOpenDialog(string actionName, string senderName, Uri address)
    {
        InitializeComponent();

        Title = string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_TitleFormat, actionName);
        MessageText.Text = string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_MessageFormat, senderName);
        HostRun.Text = address.IsAbsoluteUri ? address.IdnHost : address.OriginalString;
        PathRun.Text = address.IsAbsoluteUri ? address.PathAndQuery + address.Fragment : string.Empty;
    }

    public bool DontAskAgain => DontAskAgainCheckBox.IsChecked == true;
}
