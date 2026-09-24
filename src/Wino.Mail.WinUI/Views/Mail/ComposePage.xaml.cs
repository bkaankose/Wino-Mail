using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Controls;
using EmailValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MimeKit;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Core.Preview;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Reader;
using Wino.Editor;
using Wino.Helpers;
using Wino.Mail.Controls;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views.Abstract;

namespace Wino.Views.Mail;

public sealed partial class ComposePage : ComposePageAbstract,
    IPopoutClient,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<WinoIntelligenceAccessChanged>
{
    private const int InitialFocusRetryCount = 3;

    private bool _isPoppedOut;
    private bool _isInitialFocusHandled;
    private bool _shouldApplyInitialFocus;
    private bool _isNavigatingFrom;
    private int _isExecutingEditorShortcut;
    private bool _isSpellCheckEnabled;
    private string _spellCheckLanguageCode = string.Empty;
    private CancellationTokenSource? _editorLifecycleCancellationSource;
    private readonly Dictionary<TokenizingTextBox, RecipientSuggestionState> _recipientSuggestions = [];

    public bool SupportsPopOut => !_isPoppedOut;
    public bool HasEditorKeyboardFocus => WebViewEditor.FocusState != FocusState.Unfocused;
    public event EventHandler<PopOutRequestedEventArgs>? PopOutRequested;
    public event EventHandler<PopoutHostActionRequestedEventArgs>? HostActionRequested;

    public WebView2 GetWebView() => WebViewEditor.GetUnderlyingWebView();

    public Visibility GetPopOutButtonVisibility() => SupportsPopOut ? Visibility.Visible : Visibility.Collapsed;

    private readonly List<IDisposable> _disposables = [];
    private readonly IKeyboardShortcutService _keyboardShortcutService = WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>();
    private readonly IWinoLogger _logger = WinoApplication.Current.Services.GetRequiredService<IWinoLogger>();
    private readonly ITranslationService _translationService = WinoApplication.Current.Services.GetRequiredService<ITranslationService>();

    public ComposePage()
    {
        InitializeComponent();
        WebViewEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();
        ViewModel.CloseRequested += ViewModel_CloseRequested;
    }

    public HostedPopoutDescriptor GetPopoutDescriptor()
    {
        var title = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.Draft : ViewModel.Subject;
        var draftId = ViewModel.CurrentMailDraftItem?.MailCopy?.UniqueId.ToString("N") ?? title;

        return new HostedPopoutDescriptor(
            $"compose-{draftId}",
            title,
            1180,
            860,
            760,
            600,
            nameof(ComposePage));
    }

    public void OnPopoutStateChanged(bool isPoppedOut)
    {
        _isPoppedOut = isPoppedOut;
        Bindings.Update();
    }

    public WinoIconGlyph GetEditorThemeIcon(bool isDarkMode) => isDarkMode ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor;

    public string GetEditorThemeToolTip(bool isDarkMode) => isDarkMode ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void ToggleEditorThemeClicked(object sender, RoutedEventArgs e)
    {
        WebViewEditor.ToggleEditorTheme();
    }

    private async void EmailTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not EmailTemplate template)
            return;

        await WebViewEditor.RenderHtmlAsync(template.HtmlContent);
        comboBox.SelectedItem = null;
    }

    private async void SubjectTextBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || IsShiftKeyDown())
        {
            return;
        }

        e.Handled = true;
        await WebViewEditor.FocusEditorAsync(true);
    }

    private static bool IsShiftKeyDown()
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    private IDisposable GetSuggestionBoxDisposable(TokenizingTextBox box)
    {
        return new SuggestionBoxTextDebouncer(box, TimeSpan.FromMilliseconds(120), (senderBox, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            var query = senderBox.Text ?? string.Empty;
            if (query.Trim().Length >= 2)
            {
                _ = ResolveRecipientSuggestionsAsync(box, senderBox, query);
            }
            else
            {
                ClearRecipientSuggestions(box);
            }
        });
    }

    private void OnComposeGridDragOver(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverComposerGrid = true;
    }

    private void OnComposeGridDragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverComposerGrid = false;
    }

    private void OnFileDropGridDragOver(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverFilesDropZone = true;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Translator.ComposerAttachmentsDragDropAttach_Message;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private void OnFileDropGridDragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverFilesDropZone = false;
    }

    private async void OnFileDropGridFileDropped(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                var files = storageItems.OfType<StorageFile>();

                await AttachFiles(files);
            }
        }
        // State should be reset even when an exception occurs, otherwise the UI will be stuck in a dragging state.
        finally
        {
            ViewModel.IsDraggingOverComposerGrid = false;
            ViewModel.IsDraggingOverFilesDropZone = false;
        }
    }

    private void OnImageDropGridDragEnter(object sender, DragEventArgs e)
    {
        bool isValid = false;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // We can't use async/await here because DragUIOverride becomes inaccessible.
            // https://github.com/microsoft/microsoft-ui-xaml/issues/9296
            var files = e.DataView.GetStorageItemsAsync().GetAwaiter().GetResult().OfType<StorageFile>();

            foreach (var file in files)
            {
                if (IsValidImageFile(file))
                {
                    isValid = true;
                }
            }
        }

        e.AcceptedOperation = isValid ? DataPackageOperation.Copy : DataPackageOperation.None;

        if (isValid)
        {
            ViewModel.IsDraggingOverImagesDropZone = true;
            e.DragUIOverride.Caption = Translator.ComposerAttachmentsDragDropAttach_Message;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private void OnImageDropGridDragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverImagesDropZone = false;
    }

    private async void OnImageDropGridImageDropped(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                var files = storageItems.OfType<StorageFile>();

                var imagesInformation = new List<ImageInfo>();

                foreach (var file in files)
                {
                    if (IsValidImageFile(file))
                    {
                        imagesInformation.Add(new ImageInfo
                        {
                            Data = await GetDataURL(file),
                            Name = file.Name
                        });
                    }
                }

                await WebViewEditor.InsertImagesAsync(
                    imagesInformation.Select(image => new EditorImageInfo(image.Data, image.Name)));
            }
        }
        // State should be reset even when an exception occurs, otherwise the UI will be stuck in a dragging state.
        finally
        {
            ViewModel.IsDraggingOverComposerGrid = false;
            ViewModel.IsDraggingOverImagesDropZone = false;
        }

        static async Task<string> GetDataURL(StorageFile file)
        {
            return $"data:image/{file.FileType.Replace(".", "")};base64,{Convert.ToBase64String(await file.ToByteArrayAsync())}";
        }
    }

    private async Task AttachFiles(IEnumerable<StorageFile> files)
    {
        if (files?.Any() != true) return;

        // Convert files to MailAttachmentViewModel.
        foreach (var file in files)
        {
            var sharedFile = await file.ToSharedFileAsync();

            ViewModel.AddAttachment(sharedFile);
        }
    }

    private static bool IsValidImageFile(StorageFile file)
    {
        string[] allowedTypes = [".jpg", ".jpeg", ".png"];
        var fileType = file.FileType.ToLower();

        return allowedTypes.Contains(fileType);
    }

    private void DisposeDisposables()
    {
        if (_disposables.Count == 0)
            return;

        _disposables.ForEach(a => a.Dispose());
        _disposables.Clear();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _isNavigatingFrom = false;
        _editorLifecycleCancellationSource = new CancellationTokenSource();
        _shouldApplyInitialFocus = ConsumeInitialFocusRequest(e.Parameter as MailItemViewModel);
        _isInitialFocusHandled = false;

        _isSpellCheckEnabled = ViewModel.PreferencesService.IsComposerSpellCheckEnabled;
        _spellCheckLanguageCode = ViewModel.PreferencesService.ComposerSpellCheckLanguageCode;
        EditorCommandBar.ConfigureSpellCheckLanguages(
            _translationService.GetAvailableLanguages(),
            _spellCheckLanguageCode);

        var webView = GetWebView();

        if (webView != null)
        {
            var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");

            anim?.TryStart(webView);
        }

        _disposables.Add(GetSuggestionBoxDisposable(ToBox));
        _disposables.Add(GetSuggestionBoxDisposable(CCBox));
        _disposables.Add(GetSuggestionBoxDisposable(BccBox));
        _disposables.Add(WebViewEditor);
        WebViewEditor.ApplicationShortcutRequested -= WebViewEditor_ApplicationShortcutRequested;
        WebViewEditor.ApplicationShortcutRequested += WebViewEditor_ApplicationShortcutRequested;
        _keyboardShortcutService.KeyboardShortcutsChanged -= KeyboardShortcutService_KeyboardShortcutsChanged;
        _keyboardShortcutService.KeyboardShortcutsChanged += KeyboardShortcutService_KeyboardShortcutsChanged;

        ViewModel.GetHTMLBodyFunction = GetEditorHtmlBodyAsync;
        var editorLifecycleToken = _editorLifecycleCancellationSource.Token;
        ViewModel.RenderHtmlBodyAsyncFunc = html => RenderComposeHtmlAsync(html, editorLifecycleToken);
    }

    private void ShowCCBCCClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsCCBCCVisible = true;
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        PopOutRequested?.Invoke(this, PopOutRequestedEventArgs.Default);
    }

    private void ViewModel_CloseRequested(object? sender, EventArgs e)
    {
        if (_isPoppedOut)
        {
            HostActionRequested?.Invoke(this, new PopoutHostActionRequestedEventArgs(PopoutHostActionKind.CloseHostedInstance));
            return;
        }

        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
    }

    private void ContactTokenContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: IContactDisplayItem contact } target || string.IsNullOrWhiteSpace(contact.Address))
            return;

        WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Copy,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Copy)),
                Command = new RelayCommand(() => CopyContactAddress(contact.Address)),
                Shortcut = new ContextFlyoutShortcut("Ctrl+C", "C", Control: true),
                AutomationId = "ComposeContactCopyAddress"
            }
        ]);
    }

    private void EditorCommandBar_SpellCheckEnabledChanged(object? sender, SpellCheckEnabledChangedEventArgs e)
    {
        // The command bar saves the preference; the page keeps the value for the next editor render.
        _isSpellCheckEnabled = e.IsEnabled;
    }

    private void EditorCommandBar_SpellCheckLanguageChanged(object? sender, SpellCheckLanguageChangedEventArgs e)
    {
        _spellCheckLanguageCode = e.LanguageCode;
    }

    private static void CopyContactAddress(string address)
    {
        var package = new DataPackage();
        package.SetText(address);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// The suggestions a recipient box shows, and the inner search box showing them. The inner
    /// box is recreated as tokens are added, so it is captured with each result.
    /// </summary>
    private sealed class RecipientSuggestionState
    {
        public int Version { get; set; }
        public List<RecipientSuggestion> Items { get; set; } = [];
        public string Query { get; set; } = string.Empty;
        public AutoSuggestBox? Owner { get; set; }
    }

    private RecipientSuggestionState GetSuggestionState(TokenizingTextBox box)
    {
        if (!_recipientSuggestions.TryGetValue(box, out var state))
        {
            state = new RecipientSuggestionState();
            _recipientSuggestions[box] = state;
        }

        return state;
    }

    private ObservableCollection<AccountContact>? GetAddressCollection(TokenizingTextBox box)
        => box.Tag?.ToString() switch
        {
            "ToBox" => ViewModel.ToItems,
            "CCBox" => ViewModel.CCItems,
            "BCCBox" => ViewModel.BCCItems,
            _ => null
        };

    /// <summary>
    /// Suggests contact lists, contacts and remembered correspondents for the typed text.
    /// A slower earlier query never replaces the result of a newer one.
    /// </summary>
    private async Task ResolveRecipientSuggestionsAsync(TokenizingTextBox box, AutoSuggestBox senderBox, string query)
    {
        var state = GetSuggestionState(box);
        var version = ++state.Version;

        List<RecipientSuggestion> suggestions;
        try
        {
            suggestions = await ViewModel.RecipientSuggestionService
                .SuggestAsync(ViewModel.ComposingAccount?.Id, query)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "ComposePage.ResolveRecipientSuggestions");
            return;
        }

        await ViewModel.ExecuteUIThread(() =>
        {
            if (version != state.Version || !string.Equals(senderBox.Text, query, StringComparison.Ordinal))
                return;

            // Someone already in this field is not worth suggesting again.
            var collection = GetAddressCollection(box);
            state.Items = suggestions
                .Where(suggestion => suggestion.IsList || !ComposePageViewModel.ContainsAddress(collection!, suggestion.Address))
                .ToList();
            state.Query = query;
            state.Owner = senderBox;
            senderBox.ItemsSource = state.Items;
        });
    }

    private void ClearRecipientSuggestions(TokenizingTextBox box)
    {
        var state = GetSuggestionState(box);
        state.Version++;
        state.Items = [];
        state.Query = string.Empty;

        if (state.Owner != null)
            state.Owner.ItemsSource = null;
    }

    /// <summary>
    /// Adds every member of a picked contact list that is not already a recipient.
    /// </summary>
    private int AddListMembers(IEnumerable<AccountContact> members, ObservableCollection<AccountContact>? addressCollection)
    {
        if (addressCollection is null)
            return 0;

        var added = 0;
        foreach (var member in members)
        {
            var recipient = RecipientSuggestion.ForTypedAddress(member.PrimaryEmailAddress, member);
            if (ViewModel.TryAddRecipient(addressCollection, recipient))
                added++;
        }

        return added;
    }

    /// <summary>
    /// Splits typed or pasted text such as "Ann &lt;ann@x.com&gt;, bob@y.com" into parts.
    /// </summary>
    private static List<string> SplitRecipientText(string text)
        => (text ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// The suggestion Enter should take. When Enter comes before the debounced search has
    /// answered, the search runs now so fast typists get the same result.
    /// </summary>
    private async Task<RecipientSuggestion?> GetTopSuggestionAsync(TokenizingTextBox box, string query, ObservableCollection<AccountContact> collection)
    {
        var state = GetSuggestionState(box);

        // Only what is on screen for this exact text; a list left from shorter text would pick the wrong person.
        if (string.Equals(state.Query?.Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase) && state.Items.Count > 0)
            return state.Items[0];

        if (query.Trim().Length < 2)
            return null;

        var suggestions = await ViewModel.RecipientSuggestionService.SuggestAsync(ViewModel.ComposingAccount?.Id, query);

        // Someone already in the field is still returned, so the user hears "already added", not "invalid address".
        return suggestions.FirstOrDefault(suggestion => suggestion.IsList || !ComposePageViewModel.ContainsAddress(collection, suggestion.Address))
            ?? suggestions.FirstOrDefault();
    }

    private static bool TryParseRecipient(string text, out string address, out string? name)
    {
        address = string.Empty;
        name = null;

        if (MailboxAddress.TryParse(text, out var mailbox) && EmailValidator.Validate(mailbox.Address))
        {
            address = mailbox.Address;
            name = string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name;
            return true;
        }

        return false;
    }

    private async Task<AccountContact?> ResolveTypedRecipientAsync(string address, string? name, ObservableCollection<AccountContact> collection)
    {
        var recipient = await ViewModel.GetAddressInformationAsync(address, collection);

        // A pasted "Name <address>" keeps its name when the address is not a known contact.
        if (recipient != null && name != null && recipient is not RecipientSuggestion)
            recipient.Name = name;

        return recipient;
    }

    /// <summary>
    /// Raised for text the user commits with Enter or a delimiter. A complete address is taken
    /// as typed, even when a suggestion is showing. Partial text takes the top suggestion.
    /// A picked suggestion does not come here; see <see cref="TokenItemAdded"/>.
    /// </summary>
    private async void TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var addressCollection = GetAddressCollection(sender);
            if (addressCollection is null)
            {
                args.Cancel = true;
                return;
            }

            var parts = SplitRecipientText(args.TokenText);
            if (parts.Count == 0)
            {
                args.Cancel = true;
                return;
            }

            var parsed = parts
                .Select(part => (Text: part, IsValid: TryParseRecipient(part, out var address, out var name), Address: address, Name: name))
                .ToList();

            if (!parsed[0].IsValid)
            {
                var suggestion = parts.Count == 1 ? await GetTopSuggestionAsync(sender, parts[0], addressCollection) : null;

                if (suggestion is { IsList: true })
                {
                    args.Cancel = true;
                    if (AddListMembers(suggestion.ListMembers, addressCollection) == 0)
                        ViewModel.NotifyAddressExists();
                    ClearTypedText(sender);
                }
                else if (suggestion != null)
                {
                    if (ComposePageViewModel.ContainsAddress(addressCollection, suggestion.Address))
                    {
                        args.Cancel = true;
                        ViewModel.NotifyAddressExists();
                        ClearTypedText(sender);
                    }
                    else
                    {
                        args.Item = suggestion;
                    }
                }
                else
                {
                    args.Cancel = true;
                    ViewModel.NotifyInvalidEmail(parsed[0].Text);

                    // The box clears the text it rejected; put it back so a typo can be fixed in place.
                    var rejectedText = args.TokenText;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => sender.Text = rejectedText);
                }

                return;
            }

            var first = await ResolveTypedRecipientAsync(parsed[0].Address, parsed[0].Name, addressCollection);
            if (first == null)
            {
                args.Cancel = true;
                ViewModel.NotifyAddressExists();
                ClearTypedText(sender);
            }
            else
            {
                args.Item = first;
            }

            // The rest of a pasted list is added after the first token, keeping its order.
            var extras = parsed.Skip(1).ToList();
            if (extras.Count > 0)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    foreach (var extra in extras)
                    {
                        if (!extra.IsValid)
                        {
                            ViewModel.NotifyInvalidEmail(extra.Text);
                            continue;
                        }

                        var recipient = await ResolveTypedRecipientAsync(extra.Address, extra.Name, addressCollection);
                        if (recipient != null)
                            ViewModel.TryAddRecipient(addressCollection, recipient);
                    }
                });
            }
        }
        finally
        {
            ClearRecipientSuggestions(sender);
            deferral.Complete();
        }
    }

    /// <summary>
    /// Raised after any token is added, including a suggestion picked with the mouse or with
    /// the arrow keys and Enter, which the box inserts without asking. A contact list becomes
    /// its members here, and a second copy of an address is taken back out.
    /// </summary>
    private void TokenItemAdded(TokenizingTextBox sender, object item)
    {
        ClearRecipientSuggestions(sender);

        var addressCollection = GetAddressCollection(sender);
        if (addressCollection is null || item is not AccountContact recipient)
            return;

        if (recipient is RecipientSuggestion { IsList: true } list)
        {
            // Removed on the next tick: the box is still laying out the token it just added.
            DispatcherQueue.TryEnqueue(() =>
            {
                RemoveRecipient(addressCollection, list);
                if (AddListMembers(list.ListMembers, addressCollection) == 0)
                    ViewModel.NotifyAddressExists();
            });
            return;
        }

        if (ComposePageViewModel.ContainsAddress(addressCollection, recipient.Address, except: recipient))
        {
            DispatcherQueue.TryEnqueue(() => RemoveRecipient(addressCollection, recipient));
            ViewModel.NotifyAddressExists();
        }
    }

    // Suggestions for one contact can share its Id, so remove by reference, not by equality.
    private static void RemoveRecipient(ObservableCollection<AccountContact> collection, AccountContact recipient)
    {
        for (var index = collection.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(collection[index], recipient))
            {
                collection.RemoveAt(index);
                return;
            }
        }
    }

    private void ClearTypedText(TokenizingTextBox box)
        => DispatcherQueue.TryEnqueue(() => box.Text = string.Empty);

    /// <summary>
    /// Hides a remembered correspondent from suggestions without closing the list.
    /// </summary>
    private async void SuppressSuggestionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecipientSuggestion suggestion })
            return;

        var owner = _recipientSuggestions.FirstOrDefault(pair => pair.Value.Items.Contains(suggestion));
        if (owner.Value is { } state)
        {
            state.Items = state.Items.Where(item => !ReferenceEquals(item, suggestion)).ToList();
            if (state.Owner != null)
                state.Owner.ItemsSource = state.Items;
        }

        try
        {
            await ViewModel.SuppressSuggestionAsync(suggestion);
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "ComposePage.SuppressSuggestion");
        }
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        WebViewEditor.IsEditorDarkMode = message.IsUnderlyingThemeDark;
    }

    public async Task RefreshDraftAsync(MailItemViewModel draftMailItemViewModel)
    {
        if (draftMailItemViewModel == null || !draftMailItemViewModel.IsDraft) return;

        _shouldApplyInitialFocus = ConsumeInitialFocusRequest(draftMailItemViewModel);
        _isInitialFocusHandled = false;
        await ViewModel.RefreshDraftAsync(draftMailItemViewModel);
        await ApplyInitialFocusAsync();
    }

    private void ImportanceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: MessageImportance importance })
        {
            return;
        }

        ViewModel.SelectedMessageImportance = importance;

        // Normal is the absence of an importance header, not a third value to write.
        ViewModel.IsImportanceSelected = importance != MessageImportance.Normal;

        // Keep the toolbar icon in sync with the choice so the tab does not have to be opened to read it.
        ImportanceButtonIcon.Icon = importance switch
        {
            MessageImportance.Low => WinoIconGlyph.ArrowDown,
            _ => WinoIconGlyph.Important
        };
    }

    private async void AddressBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Automatically add current text as item if it is valid mail address.

        if (sender is TokenizingTextBox tokenizingTextBox)
        {
            if (tokenizingTextBox.Items.LastOrDefault() is not ITokenStringContainer info) return;

            var currentText = info.Text;

            if (!string.IsNullOrEmpty(currentText) && EmailValidator.Validate(currentText))
            {
                var addressCollection = GetAddressCollection(tokenizingTextBox);

                AccountContact? addedItem = null;

                if (addressCollection != null)
                    addedItem = await ViewModel.GetAddressInformationAsync(currentText, addressCollection);

                // Item has already been added.
                if (addedItem == null)
                {
                    tokenizingTextBox.Text = string.Empty;
                }
                else if (addressCollection != null)
                {
                    addressCollection.Add(addedItem);
                    tokenizingTextBox.Text = string.Empty;
                }
            }
        }
    }

    private void ComposerLoaded(object sender, RoutedEventArgs e)
    {
        if (ShouldFocusRecipients())
        {
            ToBox.Focus(FocusState.Programmatic);
        }
    }

    private void CCBBCGotFocus(object sender, RoutedEventArgs e)
    {
        if (ShouldFocusRecipients() && !_isInitialFocusHandled)
        {
            _isInitialFocusHandled = true;
            ToBox.Focus(FocusState.Programmatic);
        }
    }

    protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);

        _isNavigatingFrom = true;
        _editorLifecycleCancellationSource?.Cancel();
        ViewModel.RenderHtmlBodyAsyncFunc = null;

        try
        {
            await ViewModel.UpdateMimeChangesAsync();
        }
        catch (ObjectDisposedException) when (_isNavigatingFrom)
        {
            // A second navigation can finish tearing down the editor while this
            // navigation is still saving the draft. The editor content was already
            // captured by the winning teardown, so disposal is expected here.
        }
        finally
        {
            WebViewEditor.ApplicationShortcutRequested -= WebViewEditor_ApplicationShortcutRequested;
            _keyboardShortcutService.KeyboardShortcutsChanged -= KeyboardShortcutService_KeyboardShortcutsChanged;
            ViewModel.GetHTMLBodyFunction = null;
            DisposeDisposables();
            _editorLifecycleCancellationSource?.Dispose();
            _editorLifecycleCancellationSource = null;
        }
    }

    private async Task<string> GetEditorHtmlBodyAsync()
    {
        try
        {
            return await WebViewEditor.GetHtmlBodyAsync() ?? string.Empty;
        }
        catch (ObjectDisposedException) when (_isNavigatingFrom)
        {
            return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty;
        }
    }

    private void AttachmentContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: MailAttachmentViewModel attachment } target)
            return;

        WinoContextFlyoutHelper.Show(target, args, CreateAttachmentMenuEntries(attachment));
    }

    private void AttachmentMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MailAttachmentViewModel attachment } button)
            return;

        new WinoContextFlyout { ItemsSource = CreateAttachmentMenuEntries(attachment) }
            .ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private ContextFlyoutMenuEntry[] CreateAttachmentMenuEntries(MailAttachmentViewModel attachment) =>
    [
        new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_Open,
            Icon = CreateWinoIcon(WinoIconGlyph.OpenInNewWindow),
            Command = ViewModel.OpenAttachmentCommand,
            CommandParameter = attachment,
            AutomationId = "ComposeAttachmentOpen"
        },
        new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_Save,
            Icon = CreateWinoIcon(WinoIconGlyph.Save),
            Command = ViewModel.SaveAttachmentCommand,
            CommandParameter = attachment,
            Shortcut = new ContextFlyoutShortcut("Ctrl+S", "S", Control: true),
            AutomationId = "ComposeAttachmentSave"
        },
        ContextFlyoutSeparatorEntry.Instance,
        new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_Remove,
            Icon = CreateWinoIcon(WinoIconGlyph.Dismiss),
            Command = ViewModel.RemoveAttachmentCommand,
            CommandParameter = attachment,
            Shortcut = new ContextFlyoutShortcut("Delete", "Delete"),
            AutomationId = "ComposeAttachmentRemove"
        }
    ];

    private static ContextFlyoutIcon? CreateWinoIcon(WinoIconGlyph icon)
        => icon == WinoIconGlyph.None
            ? null
            : new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(icon));

    private void AttachmentClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MailAttachmentViewModel attachment)
        {
            ViewModel.OpenAttachmentCommand.Execute(attachment);
        }
    }

    private void AttachmentsListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete ||
            e.OriginalSource is not FrameworkElement { DataContext: MailAttachmentViewModel attachment })
            return;

        e.Handled = true;
        ViewModel.RemoveAttachmentCommand.Execute(attachment);
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceAccessChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Unregister<WinoIntelligenceAccessChanged>(this);
    }

    public void Receive(WinoIntelligenceAccessChanged message)
        => DispatcherQueue.TryEnqueue(Bindings.Update);

    // TODO: Save mime on closing the app.
    private async void OnClose(object sender, SystemNavigationCloseRequestedPreviewEventArgs e)
    {
        var deferral = e.GetDeferral();

        try
        {
            await ViewModel.UpdateMimeChangesAsync();
        }
        finally { deferral.Complete(); }
    }

    private bool ShouldFocusRecipients()
        => _shouldApplyInitialFocus && !ShouldFocusEditor();

    private static bool ConsumeInitialFocusRequest(MailItemViewModel? draft)
    {
        if (draft is not { ShouldFocusComposerOnOpen: true })
        {
            return false;
        }

        draft.ShouldFocusComposerOnOpen = false;
        return true;
    }

    private bool ShouldFocusEditor()
    {
        var inReplyTo = ViewModel.CurrentMimeMessage?.InReplyTo;

        if (string.IsNullOrWhiteSpace(inReplyTo))
        {
            inReplyTo = ViewModel.CurrentMailDraftItem?.MailCopy?.InReplyTo;
        }

        if (string.IsNullOrWhiteSpace(inReplyTo) && ViewModel.CurrentMimeMessage?.Headers.Contains(HeaderId.InReplyTo) == true)
        {
            inReplyTo = ViewModel.CurrentMimeMessage.Headers[HeaderId.InReplyTo];
        }

        return !string.IsNullOrWhiteSpace(inReplyTo);
    }

    private async Task ApplyInitialFocusAsync()
    {
        if (_isInitialFocusHandled || !_shouldApplyInitialFocus)
        {
            _isInitialFocusHandled = true;
            return;
        }

        _isInitialFocusHandled = true;

        for (var attempt = 0; attempt < InitialFocusRetryCount; attempt++)
        {
            if (ShouldFocusEditor())
            {
                await WebViewEditor.FocusEditorAsync(true);

                if (FocusManager.GetFocusedElement() is WebView2)
                {
                    return;
                }
            }
            else
            {
                ToBox.Focus(FocusState.Programmatic);

                if (ReferenceEquals(FocusManager.GetFocusedElement(), ToBox))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private async Task RenderComposeHtmlAsync(string html, CancellationToken editorLifecycleToken)
    {
        if (editorLifecycleToken.IsCancellationRequested)
            return;

        try
        {
            await WebViewEditor.ConfigureSpellCheckAsync(
                _isSpellCheckEnabled,
                _spellCheckLanguageCode);
            await WebViewEditor.ConfigureAutoCorrectAsync(ViewModel.PreferencesService.IsComposerAutoCorrectEnabled);

            await WebViewEditor.SetDefaultTypographyAsync(
                ViewModel.PreferencesService.ComposerFont,
                ViewModel.PreferencesService.ComposerFontSize);

            await ConfigureEditorApplicationShortcutsAsync();

            if (editorLifecycleToken.IsCancellationRequested)
                return;

            await WebViewEditor.RenderHtmlAsync(html);

            if (!editorLifecycleToken.IsCancellationRequested)
                await ApplyInitialFocusAsync();
        }
        catch (ObjectDisposedException) when (editorLifecycleToken.IsCancellationRequested || _isNavigatingFrom)
        {
            // Draft deletion can navigate away while initialization/rendering is
            // still in flight. Disposal is the expected completion of that work.
        }
    }

    private async void KeyboardShortcutService_KeyboardShortcutsChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => KeyboardShortcutService_KeyboardShortcutsChanged(sender, e));
            return;
        }

        try
        {
            await ConfigureEditorApplicationShortcutsAsync();
        }
        catch (ObjectDisposedException) when (_isNavigatingFrom)
        {
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "ComposePage.RefreshEditorShortcuts");
        }
    }

    private Task ConfigureEditorApplicationShortcutsAsync()
    {
        var gestures = _keyboardShortcutService.EnabledShortcutsSnapshot
            .Where(shortcut => shortcut.Mode == WinoApplicationMode.Mail && shortcut.Action == KeyboardShortcutAction.Send)
            .Select(shortcut => new EditorApplicationShortcutGesture(
                shortcut.Key,
                shortcut.ModifierKeys.HasFlag(ModifierKeys.Control),
                shortcut.ModifierKeys.HasFlag(ModifierKeys.Alt),
                shortcut.ModifierKeys.HasFlag(ModifierKeys.Shift)))
            .ToList();

        return WebViewEditor.SetApplicationShortcutsAsync(gestures);
    }

    private async void WebViewEditor_ApplicationShortcutRequested(object? sender, EditorApplicationShortcutGesture gesture)
    {
        if (Interlocked.Exchange(ref _isExecutingEditorShortcut, 1) != 0)
            return;

        var modifiers = ModifierKeys.None;
        if (gesture.Control) modifiers |= ModifierKeys.Control;
        if (gesture.Alt) modifiers |= ModifierKeys.Alt;
        if (gesture.Shift) modifiers |= ModifierKeys.Shift;

        var shortcut = _keyboardShortcutService.EnabledShortcutsSnapshot.FirstOrDefault(item =>
            item.Mode == WinoApplicationMode.Mail &&
            item.Action == KeyboardShortcutAction.Send &&
            item.ModifierKeys == modifiers &&
            string.Equals(item.Key, gesture.Key, StringComparison.OrdinalIgnoreCase));
        try
        {
            if (shortcut is null)
                return;

            await ViewModel.KeyboardShortcutHook(new KeyboardShortcutTriggerDetails
            {
                ShortcutId = shortcut.Id,
                Mode = shortcut.Mode,
                Action = shortcut.Action,
                Key = shortcut.Key,
                ModifierKeys = shortcut.ModifierKeys,
                InputContext = _isPoppedOut ? KeyboardShortcutInputContext.PopOutCompose : KeyboardShortcutInputContext.Compose,
                Sender = WebViewEditor,
                Origin = WebViewEditor
            });
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "ComposePage.EditorShortcut");
        }
        finally
        {
            Volatile.Write(ref _isExecutingEditorShortcut, 0);
        }
    }
}
