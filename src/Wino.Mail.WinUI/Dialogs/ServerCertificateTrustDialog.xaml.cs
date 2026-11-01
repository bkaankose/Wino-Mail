using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Mail.Dialogs;

public sealed partial class ServerCertificateTrustDialog : ContentDialog
{
    public ServerCertificateTrustDialog(ServerCertificateTrustPrompt prompt)
    {
        Prompt = prompt;
        CertificateDetails = DescribeCertificate(prompt.CertificateRawData);
        InitializeComponent();
    }

    internal ServerCertificateTrustPrompt Prompt { get; }
    internal string CertificateDetails { get; }
    internal bool HasCertificateDetails => CertificateDetails.Length > 0;
    internal bool IsNormal => !Prompt.IsDangerous;
    internal bool IsAcceptInitiallyEnabled => !Prompt.IsDangerous;
    internal string WarningMessage => Prompt.IsDangerous ? Prompt.Intro : string.Empty;
    internal InfoBarSeverity ProblemsSeverity => Prompt.IsDangerous ? InfoBarSeverity.Error : InfoBarSeverity.Warning;

    private void AcknowledgementChanged(object sender, RoutedEventArgs e)
        => IsPrimaryButtonEnabled = AcknowledgementCheckBox.IsChecked == true;

    private static string DescribeCertificate(byte[] rawData)
    {
        if (rawData is not { Length: > 0 })
            return string.Empty;

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(rawData);
            return certificate.ToString(verbose: true);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }
}
