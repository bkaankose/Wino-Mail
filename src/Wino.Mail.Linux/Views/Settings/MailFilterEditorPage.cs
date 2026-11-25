using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Create, edit or duplicate a mail filter (Windows MailFilterEditorPage): name, where it runs, source folder, the WHEN
/// block with dynamic conditions, the THEN block with dynamic actions, the live summary and the two switches, with
/// Cancel and Save in a bottom bar. Validation and errors are the ViewModel's info bars.
/// </summary>
internal sealed partial class MailFilterEditorPage(MailFilterEditorPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<MailFilterEditorPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private MailFilterEditorNavigationParameter? _parameter;
    private WinoSettingsGroup? _management;
    private Adw.ActionRow? _providerRow;
    private Gtk.ToggleButton? _winoToggle;
    private Gtk.ToggleButton? _providerToggle;
    private bool _applyingManagement;

#if DEBUG
    internal static MailFilterEditorPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle
        => _parameter?.FilterId is not null && !string.IsNullOrWhiteSpace(ViewModel.FilterName)
            ? ViewModel.FilterName
            : _parameter?.DuplicateFilterId is not null ? Translator.MailFilterEditor_DuplicateTitle : Translator.MailFilterEditor_CreateTitle;

    public string? PageDescription => ViewModel.Account is { } account ? account.Name ?? account.Address : null;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Page.SetBottomBar(BuildCommands());

        var name = WinoSettingsRows.Action(Translator.MailFilterEditor_Name, null, WinoIconGlyph.Tag);
        var nameEntry = WinoSettingsRows.Suffix(name, Settings.TextSuffix(vm, Translator.MailFilterEditor_Name, Translator.MailFilterEditor_NamePlaceholder,
            nameof(vm.FilterName), static s => s.FilterName, static (s, v) => s.FilterName = v, 28));
        nameEntry.SetHexpand(true);
        AddGroup(null, name);
        _nameEntry = nameEntry;

        var wino = ManagementRow(Translator.MailFilterEditor_WinoManaged, Translator.MailFilterEditor_WinoManagedDescription,
            WinoIconGlyph.Devices, MailFilterManagementType.WinoLocal, out _winoToggle);
        _providerRow = ManagementRow(Translator.MailFilterEditor_ProviderManaged, Translator.MailFilterEditor_ProviderManagedDescription,
            WinoIconGlyph.Cloud, MailFilterManagementType.Provider, out _providerToggle);
        _providerRow.SetVisible(false);
        _management = Page.AddGroup(Translator.MailFilterEditor_Management, null, wino, _providerRow);
        Settings.Bind(vm, nameof(vm.SelectedManagementType), static s => s.SelectedManagementType, _ => ApplyManagement());

        var source = Settings.Choice<MailFilterEditorPageViewModel, MailFilterFolderOption>(vm, Translator.MailFilterEditor_SourceFolder, null, WinoIconGlyph.Folder,
            static s => s.Folders, static folder => folder.DisplayName ?? string.Empty,
            nameof(vm.SelectedSourceFolder), static s => s.SelectedSourceFolder, static (s, v) => s.SelectedSourceFolder = v,
            itemsCollection: vm.Folders);
        _sourceRow = source;
        var sourceGroup = AddGroup(null, source);
        Settings.Visible(sourceGroup.Root, vm, nameof(vm.IsWinoManaged), static s => s.IsWinoManaged);

        BuildWhenBlock();
        BuildThenBlock();

        var summary = Gtk.Label.New(null);
        summary.SetWrap(true);
        summary.SetXalign(0);
        summary.AddCssClass("dim-label");
        summary.SetMarginTop(12);
        summary.SetMarginBottom(12);
        summary.SetMarginStart(12);
        summary.SetMarginEnd(12);
        var summaryGroup = AddGroup(null, WinoSettingsRows.Custom(summary));
        Settings.Bind(vm, nameof(vm.Summary), static s => s.Summary, text =>
        {
            summary.SetText(text ?? string.Empty);
            summaryGroup.Root.SetVisible(!string.IsNullOrWhiteSpace(text));
        });

        AddGroup(null,
            Settings.Switch(vm, Translator.MailFilterEditor_Enabled, Translator.MailFilterEditor_EnabledDescription, WinoIconGlyph.Sparkle,
                nameof(vm.IsEnabled), static s => s.IsEnabled, static (s, v) => s.IsEnabled = v),
            Settings.Switch(vm, Translator.MailFilterEditor_StopProcessing, Translator.MailFilterEditor_StopProcessingDescription, WinoIconGlyph.Stop,
                nameof(vm.StopProcessing), static s => s.StopProcessing, static (s, v) => s.StopProcessing = v));

        Settings.Bind(vm, nameof(vm.FilterName), static s => s.FilterName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        _parameter = parameter as MailFilterEditorNavigationParameter;
        await base.NavigateAsync(mode, parameter);
        await ViewModel.LoadAsync(parameter ?? new object());
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            _providerRow?.SetVisible(ViewModel.IsProviderAvailable);
            _management?.Root.SetSensitive(ViewModel.CanChangeManagementType);
            ApplyManagement();
            RefreshAddAction();
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        _conditionRows?.Scope.Dispose();
        _actionRows?.Scope.Dispose();
        return base.DeactivateAsync();
    }

    private Gtk.ActionBar BuildCommands()
    {
        var bar = Gtk.ActionBar.New();
        var cancel = Settings.Button(Translator.Buttons_Cancel, () => ViewModel.NavigationService.GoBack());
        var save = Settings.Button(Translator.MailFilterEditor_Save, () => Observe(ViewModel.SaveAsync()), suggested: true);
        Settings.Enabled(save, ViewModel, nameof(ViewModel.IsSaving), static s => !s.IsSaving);
        bar.PackEnd(save);
        bar.PackEnd(cancel);
        return bar;
    }

    private Adw.ActionRow ManagementRow(string title, string description, WinoIconGlyph glyph, MailFilterManagementType type, out Gtk.ToggleButton toggle)
    {
        var row = WinoSettingsRows.Action(title, description);
        row.AddPrefix(MailFilterPresentation.Tile(glyph, "accent"));
        var button = WinoIcons.ToggleButton(WinoIconGlyph.Checkmark, title);
        button.SetValign(Gtk.Align.Center);
        WinoSettingsRows.Suffix(row, button);
        row.SetActivatableWidget(button);
        Settings.Own(GtkSignals.OnToggled(button, () =>
        {
            if (_applyingManagement) return;
            if (button.GetActive()) ViewModel.SelectManagementType(type);
            ApplyManagement();
        }));
        toggle = button;
        return row;
    }

    private void ApplyManagement()
    {
        var current = ViewModel.SelectedManagementType?.Value;
        _applyingManagement = true;
        _winoToggle?.SetActive(current == MailFilterManagementType.WinoLocal);
        _providerToggle?.SetActive(current == MailFilterManagementType.Provider);
        _applyingManagement = false;
        RefreshAddAction();
    }
}
