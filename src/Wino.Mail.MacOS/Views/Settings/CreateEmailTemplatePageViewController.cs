using System.Collections.Specialized;
using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Dialogs;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Settings › Email templates › template (Windows CreateEmailTemplatePage). The window breadcrumb shows
/// "Email Templates ›" and the template name, with "Edit template" (or "Create a new e-mail template")
/// under it. Name, Description and Subject cards carry 280pt fields; the Template content header precedes the
/// shared Format toolbar fused to the HTML editor, which fills the remaining height (at least 270pt).
/// The footer holds Delete (existing templates only) and the primary Save (Cmd+S), which pass the
/// editor HTML to the ViewModel; both return to the list. Navigation parameter: the template id, or none.
/// </summary>
public sealed class CreateEmailTemplatePageViewController(
    CreateEmailTemplatePageViewModel viewModel,
    IExternalLauncher launcher,
    IPreferencesService preferences,
    IDispatcher dispatcher,
    IWinoLogger logger)
    : SettingsPageViewController<CreateEmailTemplatePageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const double MinimumEditorHeight = 270;
    private const double FieldWidth = 280;

    private WinoRichTextEditorView _editor = null!;
    private NSTextField _nameField = null!;
    private NSTokenField _toField = null!;
    private TemplateToTokenDelegate? _toDelegate;
    private NSButton _save = null!;
    private NSButton _delete = null!;
    private bool _busy;
    private bool _editorDisposed;
    private bool _syncingRecipients;

    public string? PageTitle => !string.IsNullOrWhiteSpace(ViewModel.TemplateName)
        ? ViewModel.TemplateName.Trim()
        : ViewModel.IsExistingTemplate ? Translator.SettingsEmailTemplates_EditPageTitle : Translator.SettingsEmailTemplates_CreatePageTitle;

    public string? PageDescription => ViewModel.IsExistingTemplate
        ? Translator.SettingsEmailTemplates_EditPageTitle
        : Translator.SettingsEmailTemplates_NewTemplateDescription;

    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || _busy;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // ---- Name and description ----
        _nameField = Bind.TextField(vm, nameof(vm.TemplateName), s => s.TemplateName, (s, v) => s.TemplateName = v,
            Translator.SettingsEmailTemplates_NamePlaceholder, FieldWidth);
        WinoAccessibility.Label(_nameField, Translator.SettingsEmailTemplates_NameTitle);
        _nameField.Changed += (_, _) => Refresh();
        var nameCard = Add(Card(Translator.SettingsEmailTemplates_NameTitle, Translator.SettingsEmailTemplates_NameDescription, WinoIconGlyph.Tag, _nameField));

        var descriptionField = Bind.TextField(vm, nameof(vm.TemplateDescription), s => s.TemplateDescription, (s, v) => s.TemplateDescription = v,
            Translator.SettingsEmailTemplates_DescriptionPlaceholder, FieldWidth);
        WinoAccessibility.Label(descriptionField, Translator.SettingsEmailTemplates_DescriptionTitle);
        var descriptionCard = Add(Card(Translator.SettingsEmailTemplates_DescriptionTitle, Translator.SettingsEmailTemplates_DescriptionDescription, WinoIconGlyph.Note, descriptionField));

        var subjectField = Bind.TextField(vm, nameof(vm.TemplateSubject), s => s.TemplateSubject, (s, v) => s.TemplateSubject = v,
            Translator.SettingsEmailTemplates_SubjectPlaceholder, FieldWidth);
        WinoAccessibility.Label(subjectField, Translator.SettingsEmailTemplates_SubjectTitle);
        var subjectCard = Add(Card(Translator.SettingsEmailTemplates_SubjectTitle, Translator.SettingsEmailTemplates_SubjectDescription, WinoIconGlyph.Mail, subjectField));

        _toField = new NSTokenField(new CoreGraphics.CGRect(0, 0, FieldWidth, 24))
        {
            Bordered = true,
            Bezeled = true,
            Editable = true,
            Font = WinoStyle.Body,
            TokenStyle = NSTokenStyle.Rounded,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _toField.WidthAnchor.ConstraintEqualTo((nfloat)FieldWidth).Active = true;
        _toField.CharacterSet = NSCharacterSet.FromString(",;");
        _toDelegate = new TemplateToTokenDelegate(this);
        _toField.Delegate = _toDelegate;
        WinoAccessibility.Label(_toField, Translator.SettingsEmailTemplates_ToTitle);
        var toCard = Add(Card(Translator.SettingsEmailTemplates_ToTitle, Translator.SettingsEmailTemplates_ToDescription, WinoIconGlyph.People, _toField));
        NotifyCollectionChangedEventHandler recipientsChanged = (_, _) =>
        {
            if (_syncingRecipients) return;
            WriteToTokens();
        };
        vm.TemplateToItems.CollectionChanged += recipientsChanged;
        Bindings.Own(new ActionDisposable(() => vm.TemplateToItems.CollectionChanged -= recipientsChanged));
        WriteToTokens();

        var signaturePopUp = Bind.PopUp<CreateEmailTemplatePageViewModel, EmailTemplateSignaturePickerItem>(vm,
            source => source.SignaturePickerItems, item => item.DisplayName,
            nameof(vm.SelectedSignatureItem), source => source.SelectedSignatureItem, (source, value) => source.SelectedSignatureItem = value,
            itemsProperty: nameof(vm.SignaturePickerItems), width: FieldWidth);
        WinoAccessibility.Label(signaturePopUp, Translator.SettingsEmailTemplates_SignatureTitle);
        var signatureCard = Add(Card(Translator.SettingsEmailTemplates_SignatureTitle, Translator.SettingsEmailTemplates_SignatureDescription, WinoIconGlyph.Signature, signaturePopUp));

        // ---- Template content ----
        var contentTitle = WinoStyle.Label(Translator.SettingsEmailTemplates_ContentTitle, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        var contentDescription = WinoStyle.Label(Translator.SettingsEmailTemplates_ContentDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var contentHeader = WinoLayout.VStack(0, contentTitle, contentDescription);
        contentHeader.Alignment = NSLayoutAttribute.Leading;
        contentHeader.EdgeInsets = new NSEdgeInsets(14, 1, 6, 0);
        Add(contentHeader);

        _editor = new WinoRichTextEditorView(new WinoRichTextEditorOptions(Fused: true, Highlight: true, Table: true, Emoji: true, ClearFormatting: false),
            Translator.SettingsEmailTemplates_ContentTitle, launcher, preferences, ReportError);
        _editor.EditorSurface.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)MinimumEditorHeight).Active = true;
        _editor.ReadyChanged += (_, _) => Refresh();
        Add(_editor);

        // ---- Footer: Delete (existing only) and Save ----
        _delete = SettingsBinder.CreateButton(Translator.Buttons_Delete, icon: WinoIconGlyph.Delete);
        _delete.AttributedTitle = new Foundation.NSAttributedString(Translator.Buttons_Delete, new NSStringAttributes { ForegroundColor = WinoStyle.Critical, Font = _delete.Font });
        _delete.ContentTintColor = WinoStyle.Critical;
        WinoAccessibility.Label(_delete, Translator.Buttons_Delete);
        Bind.OnActivated(_delete, () => Run(DeleteAsync()));
        Bind.Visible(_delete, vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate);

        _save = SettingsBinder.CreateButton(Translator.Buttons_Save, primary: true, icon: WinoIconGlyph.Save);
        _save.KeyEquivalent = "s";
        _save.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask;
        _save.ToolTip = $"{Translator.Buttons_Save} (⌘S)";
        Bind.OnActivated(_save, () => Run(SaveAsync()));
        EventHandler accentChanged = (_, _) => _save.BezelColor = WinoStyle.Accent;
        WinoStyle.AccentChanged += accentChanged;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= accentChanged));

        var footer = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), _delete, _save);
        footer.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        Add(footer);

        // The editor takes the height the other rows leave in the window.
        foreach (var view in new NSView[] { nameCard, descriptionCard, subjectCard, toCard, signatureCard, contentHeader, footer })
            view.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
        // Cards have no intrinsic height (only a minimum), so hugging alone left them free to absorb
        // the spare height; keep them at their minimum unless their content needs more.
        foreach (var card in new NSView[] { nameCard, descriptionCard, subjectCard, toCard, signatureCard })
        {
            var compact = card.HeightAnchor.ConstraintEqualTo((nfloat)WinoSettingsStyle.CardMinHeight);
            compact.Priority = 740;
            compact.Active = true;
        }
        _editor.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        contentHeader.SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        footer.SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        Page.SetHuggingPriority(1, NSLayoutConstraintOrientation.Vertical);
        if (View is NSScrollView { DocumentView: { } document } scroll)
            document.HeightAnchor.ConstraintGreaterThanOrEqualTo(scroll.ContentView.HeightAnchor).Active = true;

        // The window title and subtitle follow the name and whether the template already exists.
        Bind.Bind(vm, nameof(vm.TemplateName), s => s.TemplateName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Bind.Bind(vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Refresh();
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        var html = await ViewModel.LoadAsync(parameter!);
        var dark = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            dark = WinoRichTextEditorView.IsDark(View.EffectiveAppearance);
            if (!ViewModel.IsExistingTemplate) View.Window?.MakeFirstResponder(_nameField);
        });
        await _editor.LoadAsync(html, dark);
    }

    protected override async Task DeactivateAsync()
    {
        await base.DeactivateAsync();
        await DisposeEditorAsync();
    }

    private bool CanSave => _editor.IsReady && !_busy;

    private void Refresh()
    {
        _save.Enabled = CanSave;
        _delete.Enabled = !_busy;
    }

    /// <summary>The ViewModel validates the name (warning info bar), saves and goes back.</summary>
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        _busy = true;
        Refresh();
        try
        {
            var html = await _editor.GetHtmlBodyAsync() ?? string.Empty;
            await ViewModel.SaveAsync(html);
        }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _busy = false;
                if (!_editorDisposed) Refresh();
            });
        }
    }

    /// <summary>The ViewModel confirms, deletes and goes back.</summary>
    private async Task DeleteAsync()
    {
        if (_busy || !ViewModel.IsExistingTemplate) return;
        _busy = true;
        Refresh();
        try { await ViewModel.DeleteAsync(); }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _busy = false;
                if (!_editorDisposed) Refresh();
            });
        }
    }

    private async Task DisposeEditorAsync()
    {
        if (_editorDisposed) return;
        _editorDisposed = true;
        try { await _editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { ReportError(exception); }
    }

    private void WriteToTokens()
    {
        _syncingRecipients = true;
        try
        {
            var addresses = new List<NSObject>();
            foreach (var contact in ViewModel.TemplateToItems)
            {
                if (string.IsNullOrWhiteSpace(contact.Address)) continue;
                addresses.Add(new NSString(contact.Address));
            }
            _toField.ObjectValue = NSArray.FromNSObjects(addresses.ToArray());
        }
        finally { _syncingRecipients = false; }
    }

    private void ReadToTokens()
    {
        if (_syncingRecipients) return;
        _syncingRecipients = true;
        try
        {
            ViewModel.TemplateToItems.Clear();
            if (_toField.ObjectValue is not NSArray tokens) return;
            for (nuint i = 0; i < tokens.Count; i++)
            {
                var text = tokens.GetItem<NSString>(i)?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!ComposeRecipientRules.TryParseTyped(text, out var address, out var name)
                    || !MailAccountAddressValidator.IsValid(address))
                {
                    ViewModel.NotifyInvalidEmail(text);
                    continue;
                }
                ViewModel.TryAddRecipient(new AccountContact
                {
                    Address = address,
                    Name = name ?? string.Empty,
                    DisplayName = string.IsNullOrWhiteSpace(name) ? address : name
                });
            }
        }
        finally
        {
            _syncingRecipients = false;
            WriteToTokens();
        }
    }

    private sealed class TemplateToTokenDelegate(CreateEmailTemplatePageViewController owner) : NSTokenFieldDelegate
    {
        public override void Changed(NSNotification notification) => owner.ReadToTokens();

        public override NSObject[] GetCompletionStrings(NSControl control, NSObject[] editingItems, string edited, nuint index)
            => [];
    }
}
