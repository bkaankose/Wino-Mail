using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Common;

namespace Wino.Mail.ViewModels;

public partial class SignatureAndEncryptionPageViewModel : MailBaseViewModel
{
    private readonly ISmimeCertificateService _smimeCertificateService;
    private readonly IDialogServiceBase _dialogService;
    private readonly IFileService _fileService;

    public ObservableCollection<X509Certificate2> PersonalCertificates { get; } = [];
    public ObservableCollection<X509Certificate2> RecipientCertificates { get; } = [];
    public List<X509Certificate2> SelectedPersonalCertificates { get; } = [];
    public List<X509Certificate2> SelectedRecipientCertificates { get; } = [];

    public bool PersonalCertificatesEmpty => PersonalCertificates.Count == 0;

    public SignatureAndEncryptionPageViewModel(
        IDialogServiceBase dialogService,
        ISmimeCertificateService smimeCertificateService,
        IFileService fileService
    )
    {
        _dialogService = dialogService;
        _fileService = fileService;
        _smimeCertificateService = smimeCertificateService;

        PersonalCertificates.CollectionChanged += (s, e) => { OnPropertyChanged(nameof(PersonalCertificatesEmpty)); };
        LoadAllCertificates();
    }

    private void LoadAllCertificates()
    {
        PersonalCertificates.Clear();
        var personalCerts = _smimeCertificateService.GetCertificates();
        foreach (var cert in personalCerts)
        {
            PersonalCertificates.Add(cert);
        }

        // Recipient certificates
        RecipientCertificates.Clear();
        var recipientCerts = _smimeCertificateService.GetCertificates(storeName: StoreName.AddressBook);
        foreach (var cert in recipientCerts)
        {
            RecipientCertificates.Add(cert);
        }
    }

    [RelayCommand]
    public async Task ImportPersonalCertificatesAsync()
    {
        await ImportCertificates(StoreName.My);
    }

    [RelayCommand]
    public async Task ImportRecipientCertificatesAsync()
    {
        await ImportCertificates(StoreName.AddressBook);
    }

    private async Task ImportCertificates(StoreName storeName)
    {
        var files = await PickCertificateFilesAsync();
        var failedImports = new List<string>();
        var successCount = 0;
        foreach (var file in files)
        {
            string password = null;
            if (file.FileExtension.Equals(".pfx") || file.FileExtension.Equals(".p12"))
            {
                password = await PromptForPasswordAsync(file.FileName);
            }

            try
            {
                _smimeCertificateService.ImportCertificate(file.FileExtension, file.Data, password,
                    storeName: storeName);
                successCount++;
            }
            catch (Exception ex)
            {
                failedImports.Add($"{file.FileName}: {ex.Message}");
            }
        }
        LoadAllCertificates();
        if (successCount > 0)
        {
            _dialogService.InfoBarMessage(
                string.Format(Translator.Smime_ImportCertificates_Success),
                Translator.GeneralTitle_Info,
                InfoBarMessageType.Success);
        }
        if (failedImports.Count > 0)
        {
            await _dialogService.ShowMessageAsync(
                $"{Translator.Smime_ImportCertificates_Error}\n\n{string.Join("\n", failedImports)}",
                Translator.GeneralTitle_Warning,
                Core.Domain.Enums.WinoCustomMessageDialogIcon.Warning);
        }
    }

    [RelayCommand]
    public async Task RemovePersonalCertificatesAsync()
    {
        await RemoveCertificatesAsync(SelectedPersonalCertificates, StoreName.My);
    }

    [RelayCommand]
    public async Task RemoveRecipientCertificatesAsync()
    {
        await RemoveCertificatesAsync(SelectedRecipientCertificates, StoreName.AddressBook);
    }

    private async Task RemoveCertificatesAsync(List<X509Certificate2> certificates, StoreName storeName)
    {
        if (certificates.Any())
        {
            var confirm = await ConfirmAsync(string.Format(Translator.Smime_RemoveCertificates_Confirm,
                string.Join(", ", certificates.Select(cert => cert.Subject))));
            if (confirm)
            {
                foreach (var cert in certificates)
                {
                    _smimeCertificateService.RemoveCertificate(cert.Thumbprint, storeName: storeName);
                }

                LoadAllCertificates();
                _dialogService.InfoBarMessage(
                    Translator.Smime_RemoveCertificates_Success,
                    Translator.GeneralTitle_Info,
                    InfoBarMessageType.Success
                );
            }
        }
    }

    [RelayCommand]
    public async Task ExportPersonalCertificatesAsync()
    {
        await ExportCertificatesAsync(SelectedPersonalCertificates);
    }

    [RelayCommand]
    public async Task ExportRecipientCertificatesAsync()
    {
        await ExportCertificatesAsync(SelectedRecipientCertificates);
    }

    // Export logic for .cer or .pem
    private async Task ExportCertificatesAsync(IEnumerable<X509Certificate2> cert)
    {
        var failedExports = new List<string>();
        var successCount = 0;
        foreach (var certificate in cert)
        {
            var fileName = $"{certificate.Subject.Replace("CN=", "")}.cer";
            var path = await _dialogService.PickFilePathAsync(fileName);
            if (path != null)
            {
                await using var stream = await _fileService.GetFileStreamAsync(path, fileName);
                if (stream != null)
                {
                    try
                    {
                        var certificateData = certificate.Export(X509ContentType.Cert);
                        await stream.WriteAsync(certificateData, 0, certificateData.Length);
                        await stream.FlushAsync();
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failedExports.Add($"{certificate.Subject}: {ex.Message}");
                    }
                }
                else
                {
                    failedExports.Add($"{certificate.Subject}: File stream error");
                }
            }
        }
        if (successCount > 0)
        {
            _dialogService.InfoBarMessage(
                Translator.Smime_ExportCertificates_Success,
                Translator.GeneralTitle_Info,
                InfoBarMessageType.Success
            );
        }
        if (failedExports.Count > 0)
        {
            await _dialogService.ShowMessageAsync(
                $"{Translator.Smime_ExportCertificates_Error}\n\n{string.Join("\n", failedExports)}",
                Translator.GeneralTitle_Warning,
                Core.Domain.Enums.WinoCustomMessageDialogIcon.Warning);
        }
    }

    private async Task ShowCertificateDetailsAsync(X509Certificate2 cert)
    {
        var details = string.Format(Translator.Smime_CertificateDetails, cert.Subject, cert.Issuer, cert.NotBefore,
            cert.NotAfter, cert.Thumbprint);
        await _dialogService.ShowMessageAsync(details, Translator.GeneralTitle_Info,
            Core.Domain.Enums.WinoCustomMessageDialogIcon.Information);
    }

    // Confirmation dialog
    private async Task<bool> ConfirmAsync(string message)
    {
        return await _dialogService.ShowConfirmationDialogAsync(message, Translator.Smime_Confirm_Title,
            Translator.Buttons_Yes);
    }

    // File picker for importing certificates
    private async Task<List<SharedFile>> PickCertificateFilesAsync()
    {
        return await _dialogService.PickFilesAsync(".pfx", ".p12", ".cer", ".crt");
    }

    // Ask for password for .pfx/.p12
    private async Task<string> PromptForPasswordAsync(string fileName)
    {
        return await _dialogService.ShowTextInputDialogAsync("",
            Translator.Smime_CertificatePassword_Title,
            string.Format(Translator.Smime_CertificatePassword_Placeholder, fileName), Translator.Buttons_OK);
    }
}
