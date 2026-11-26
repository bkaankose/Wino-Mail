#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// State of the import dialog: the scan with the client being read, then the found accounts to pick from. Call
/// <see cref="ScanAsync"/> on the UI thread; progress and results arrive on it.
/// </summary>
public sealed partial class MailClientImportDialogModel : ObservableObject
{
    private readonly IMailClientAccountImportService _importService;

    public MailClientImportDialogModel(IMailClientAccountImportService importService)
    {
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
    }

    public ObservableCollection<MailClientImportItem> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoAccountsMessageVisible))]
    [NotifyPropertyChangedFor(nameof(IsAccountListVisible))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoAccountsMessageVisible))]
    [NotifyPropertyChangedFor(nameof(IsAccountListVisible))]
    public partial bool IsScanCompleted { get; set; }

    [ObservableProperty]
    public partial string ScanStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanImport { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool IsNoAccountsMessageVisible => IsScanCompleted && Items.Count == 0;
    public bool IsAccountListVisible => IsScanCompleted && Items.Count > 0;

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (IsScanning)
            return;

        IsScanning = true;
        IsScanCompleted = false;
        ErrorMessage = string.Empty;
        ClearItems();

        var progress = new Progress<MailClientImportSource>(source
            => ScanStatus = string.Format(Translator.MailClientImport_Scanning, MailClientImportItem.GetSourceName(source)));

        try
        {
            var candidates = await _importService.ScanAsync(progress, cancellationToken);

            foreach (var candidate in candidates.OrderBy(c => c.IsAlreadyAdded).ThenBy(c => c.Source))
            {
                var item = new MailClientImportItem(candidate);
                item.PropertyChanged += ItemPropertyChanged;
                Items.Add(item);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scanning other mail clients for accounts failed.");
            ErrorMessage = Translator.MailClientImport_ScanFailed;
        }
        finally
        {
            IsScanning = false;
        }

        ScanStatus = string.Empty;
        IsScanCompleted = true;
        UpdateCanImport();
    }

    public List<MailClientImportCandidate> GetSelectedCandidates()
        => Items.Where(i => i.IsSelected && i.CanSelect).Select(i => i.Candidate).ToList();

    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailClientImportItem.IsSelected))
            UpdateCanImport();
    }

    private void UpdateCanImport() => CanImport = Items.Any(i => i.IsSelected && i.CanSelect);

    private void ClearItems()
    {
        foreach (var item in Items)
            item.PropertyChanged -= ItemPropertyChanged;

        Items.Clear();
    }
}
