using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Arranges the reader command bar. Changes stay in a working copy until Apply.
/// </summary>
public partial class ReaderActionsPageViewModel : MailBaseViewModel
{
    private const bool DefaultShowActionLabels = true;

    private readonly IPreferencesService _preferencesService;
    private readonly IMailDialogService _dialogService;
    private readonly ReaderCommandContext _previewContext;

    private ReaderCommandLayoutEntry[] _savedEntries;
    private bool _savedShowActionLabels;

    public ObservableCollection<ReaderCommandLayoutItemViewModel> Items { get; } = [];

    public string PreviewSubject => Translator.ReaderActions_PreviewSubject;
    public string PreviewSenderName => "John Doe";
    public string PreviewSenderAddress => "john.doe@example.com";
    public string PreviewSenderDisplay => $"{PreviewSenderName} <{PreviewSenderAddress}>";
    public string PreviewDate { get; } = DateTime.Now.ToString("f");

    [ObservableProperty]
    public partial IReadOnlyList<IMenuOperation> PreviewMenuItems { get; set; } = Array.Empty<IMenuOperation>();

    [ObservableProperty]
    public partial bool ShowActionLabels { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetToDefaultCommand))]
    public partial bool IsDefaultLayout { get; set; }

    public ReaderActionsPageViewModel(IPreferencesService preferencesService,
                                      IMailDialogService dialogService,
                                      IPlatformCapabilities platformCapabilities)
    {
        _preferencesService = preferencesService;
        _dialogService = dialogService;

        _previewContext = new ReaderCommandContext
        {
            HasMailItem = true,
            IsRead = true,
            FolderType = SpecialFolderType.Inbox,
            HasMimeMessage = true,
            CanExportPdf = platformCapabilities.PdfExport,
            CanPrint = platformCapabilities.Printing
        };

        _savedEntries = ReaderCommandLayout.Parse(preferencesService.ReaderCommandLayout);
        _savedShowActionLabels = preferencesService.IsShowActionLabelsEnabled;

        Items.CollectionChanged += ItemsCollectionChanged;
        Load(_savedEntries, _savedShowActionLabels);
    }

    /// <summary>
    /// Moves an item by one or more places. Keyboard reordering uses it; drag and drop reorders the collection directly.
    /// </summary>
    public bool MoveItem(ReaderCommandLayoutItemViewModel item, int offset)
    {
        var index = Items.IndexOf(item);
        var target = index + offset;

        if (index < 0 || target < 0 || target >= Items.Count)
            return false;

        Items.Move(index, target);
        return true;
    }

    partial void OnShowActionLabelsChanged(bool value) => Refresh();

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Apply()
    {
        var entries = GetCurrentEntries();

        _preferencesService.ReaderCommandLayout = ReaderCommandLayout.Serialize(entries);
        _preferencesService.IsShowActionLabelsEnabled = ShowActionLabels;

        _savedEntries = entries;
        _savedShowActionLabels = ShowActionLabels;
        Refresh();

        _dialogService.InfoBarMessage(Translator.ReaderActions_SavedTitle, Translator.ReaderActions_SavedMessage, InfoBarMessageType.Success);
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Discard() => Load(_savedEntries, _savedShowActionLabels);

    [RelayCommand(CanExecute = nameof(CanResetToDefault))]
    private void ResetToDefault() => Load(ReaderCommandLayout.CreateDefault(), DefaultShowActionLabels);

    private bool CanResetToDefault() => !IsDefaultLayout;

    private void Load(IEnumerable<ReaderCommandLayoutEntry> entries, bool showActionLabels)
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= ItemPropertyChanged;
        }

        Items.CollectionChanged -= ItemsCollectionChanged;
        Items.Clear();

        foreach (var entry in entries)
        {
            var item = ReaderCommandLayoutItemViewModel.Create(entry);
            item.PropertyChanged += ItemPropertyChanged;
            Items.Add(item);
        }

        Items.CollectionChanged += ItemsCollectionChanged;

        ShowActionLabels = showActionLabels;
        Refresh();
    }

    private void ItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        // GridView reordering removes and re-inserts the same item, so handlers follow the items.
        foreach (ReaderCommandLayoutItemViewModel item in e.OldItems ?? Array.Empty<ReaderCommandLayoutItemViewModel>())
        {
            item.PropertyChanged -= ItemPropertyChanged;
        }

        foreach (ReaderCommandLayoutItemViewModel item in e.NewItems ?? Array.Empty<ReaderCommandLayoutItemViewModel>())
        {
            item.PropertyChanged -= ItemPropertyChanged;
            item.PropertyChanged += ItemPropertyChanged;
        }

        Refresh();
    }

    private void ItemPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderCommandLayoutItemViewModel.IsVisible))
        {
            Refresh();
        }
    }

    private ReaderCommandLayoutEntry[] GetCurrentEntries() => Items.Select(item => item.ToEntry()).ToArray();

    private void Refresh()
    {
        var entries = GetCurrentEntries();

        IsDirty = ShowActionLabels != _savedShowActionLabels || !entries.SequenceEqual(_savedEntries);
        IsDefaultLayout = ShowActionLabels == DefaultShowActionLabels && entries.SequenceEqual(ReaderCommandLayout.CreateDefault());
        PreviewMenuItems = ReaderCommandLayout.CreateMenuItems(entries, _previewContext);
    }
}
