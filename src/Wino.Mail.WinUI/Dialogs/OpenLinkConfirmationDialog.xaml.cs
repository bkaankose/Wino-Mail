using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Dialogs;

/// <summary>
/// Shows where a link from a message goes, with the tracking parameters Wino removed struck through.
/// </summary>
public sealed partial class OpenLinkConfirmationDialog : ContentDialog
{
    public OpenLinkConfirmationDialog(string? linkText, Uri originalUri, Uri cleanedUri, int removedParameterCount, string? browserName)
    {
        InitializeComponent();

        IntroText.Text = OpenLinkConfirmationText.GetIntro(linkText);

        if (cleanedUri.IsAbsoluteUri)
        {
            DestinationBaseRun.Text = cleanedUri.GetLeftPart(UriPartial.Query);
            RemovedParametersRun.Text = OpenLinkConfirmationText.GetRemovedQuery(originalUri, cleanedUri);
            DestinationFragmentRun.Text = cleanedUri.Fragment;
        }
        else
        {
            DestinationBaseRun.Text = cleanedUri.OriginalString;
        }

        var footer = OpenLinkConfirmationText.GetFooter(removedParameterCount, browserName);
        FooterText.Text = footer;
        FooterPanel.Visibility = string.IsNullOrEmpty(footer) ? Visibility.Collapsed : Visibility.Visible;
    }
}
