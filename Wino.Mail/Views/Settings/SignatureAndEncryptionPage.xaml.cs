using System.Linq;
using Wino.Views.Abstract;
using Windows.UI.Xaml.Controls;
using System.Security.Cryptography.X509Certificates;

namespace Wino.Views.Settings;

public sealed partial class SignatureAndEncryptionPage : SignatureAndEncryptionPageAbstract
{
    public SignatureAndEncryptionPage()
    {
        InitializeComponent();
    }

    private void PersonalCertList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems.OfType<X509Certificate2>())
        {
            ViewModel.SelectedPersonalCertificates.Remove(item);
        }

        ViewModel.SelectedPersonalCertificates.AddRange(e.AddedItems.OfType<X509Certificate2>());
    }

    private void RecipientCertList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems.OfType<X509Certificate2>())
        {
            ViewModel.SelectedRecipientCertificates.Remove(item);
        }

        ViewModel.SelectedRecipientCertificates.AddRange(e.AddedItems.OfType<X509Certificate2>());
    }
}
