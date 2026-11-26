using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Settings;

public sealed partial class CreateEmailTemplatePage : CreateEmailTemplatePageAbstract
{
    private readonly ObservableCollection<RecipientSuggestion> _templateToSuggestions = [];
    private SuggestionBoxTextDebouncer? _suggestionDebouncer;

    public CreateEmailTemplatePage()
    {
        InitializeComponent();
        TemplateEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();
        _suggestionDebouncer = new SuggestionBoxTextDebouncer(TemplateToBox, ComposeRecipientRules.SuggestionDelay, OnTemplateToTextChanged);
    }

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var htmlContent = await ViewModel.LoadAsync(e.Parameter);
        var preferences = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();

        try
        {
            await TemplateEditor.Session.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
            await TemplateEditor.Session.RenderHtmlAsync(htmlContent ?? string.Empty);
        }
        catch (ObjectDisposedException)
        {
            // The page was left before the editor showed the template.
            return;
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The template editor failed to load the template.");
        }

        if (!ViewModel.IsExistingTemplate)
        {
            TemplateNameTextBox.Focus(FocusState.Programmatic);
        }
    }

    protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        _suggestionDebouncer?.Dispose();
        _suggestionDebouncer = null;
        await TemplateEditor.Session.DisposeAsync();
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        CommitTypedTemplateTo();

        string htmlContent;

        try
        {
            htmlContent = await TemplateEditor.Session.GetHtmlBodyAsync() ?? string.Empty;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The editor never showed the template, so its content would replace the saved one with nothing.
            return;
        }

        await ViewModel.SaveAsync(htmlContent);
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteAsync();
    }

    private void EditorLinkOpenRequested(object? sender, Uri uri) => EditorLinkLauncher.Open(uri);

    private async void TemplateToTokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var text = args.TokenText?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                args.Cancel = true;
                return;
            }

            if (ComposeRecipientRules.TryParseTyped(text, out var address, out var name)
                && MailAccountAddressValidator.IsValid(address))
            {
                if (ComposePageViewModel.ContainsAddress(ViewModel.TemplateToItems, address))
                {
                    args.Cancel = true;
                    ViewModel.NotifyAddressExists();
                    ClearTypedText(sender);
                    return;
                }

                args.Item = new AccountContact
                {
                    Address = address,
                    Name = name ?? string.Empty,
                    DisplayName = string.IsNullOrWhiteSpace(name) ? address : name
                };
                return;
            }

            var suggestion = await GetTopSuggestionAsync(text);
            if (suggestion is { IsList: true })
            {
                args.Cancel = true;
                if (AddListMembers(suggestion.ListMembers) == 0)
                    ViewModel.NotifyAddressExists();
                ClearTypedText(sender);
            }
            else if (suggestion != null)
            {
                if (ComposePageViewModel.ContainsAddress(ViewModel.TemplateToItems, suggestion.Address))
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
                ViewModel.NotifyInvalidEmail(text);
                var rejected = args.TokenText;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => sender.Text = rejected);
            }
        }
        finally
        {
            ClearSuggestions();
            deferral.Complete();
        }
    }

    private void TemplateToTokenItemAdded(TokenizingTextBox sender, object item)
    {
        ClearSuggestions();
        if (item is not AccountContact recipient)
            return;

        if (recipient is RecipientSuggestion { IsList: true } list)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RemoveByReference(list);
                if (AddListMembers(list.ListMembers) == 0)
                    ViewModel.NotifyAddressExists();
            });
            return;
        }

        if (ComposePageViewModel.ContainsAddress(ViewModel.TemplateToItems, recipient.Address, except: recipient))
        {
            DispatcherQueue.TryEnqueue(() => RemoveByReference(recipient));
            ViewModel.NotifyAddressExists();
        }
    }

    private void TemplateToLostFocus(object sender, RoutedEventArgs e)
        => CommitTypedTemplateTo();

    private void OnTemplateToTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        var query = sender.Text ?? string.Empty;
        if (!ComposeRecipientRules.ShouldSuggest(query))
        {
            ClearSuggestions();
            return;
        }

        _ = ResolveSuggestionsAsync(sender, query);
    }

    private async Task ResolveSuggestionsAsync(AutoSuggestBox senderBox, string query)
    {
        try
        {
            var suggestions = await ViewModel.RecipientSuggestionService.SuggestAsync(ViewModel.SuggestionAccountId, query);
            var filtered = ComposeRecipientRules.ExcludeExisting(suggestions, ViewModel.TemplateToItems);
            _templateToSuggestions.Clear();
            foreach (var item in filtered)
                _templateToSuggestions.Add(item);
            senderBox.ItemsSource = _templateToSuggestions;
        }
        catch (Exception)
        {
            ClearSuggestions();
        }
    }

    private void CommitTypedTemplateTo()
    {
        var typed = TemplateToBox.Text?.Trim();
        if (!ComposeRecipientRules.IsCompleteAddress(typed))
            return;

        if (!ComposeRecipientRules.TryParseTyped(typed, out var address, out var name)
            || !MailAccountAddressValidator.IsValid(address))
            return;

        if (!ViewModel.TryAddRecipient(new AccountContact
            {
                Address = address,
                Name = name ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(name) ? address : name
            }))
        {
            ViewModel.NotifyAddressExists();
        }

        TemplateToBox.Text = string.Empty;
        ClearSuggestions();
    }

    private async Task<RecipientSuggestion?> GetTopSuggestionAsync(string query)
    {
        if (_templateToSuggestions.Count > 0)
            return _templateToSuggestions[0];

        if (!ComposeRecipientRules.ShouldSuggest(query))
            return null;

        var suggestions = await ViewModel.RecipientSuggestionService.SuggestAsync(ViewModel.SuggestionAccountId, query);
        return ComposeRecipientRules.ExcludeExisting(suggestions, ViewModel.TemplateToItems).FirstOrDefault()
            ?? suggestions.FirstOrDefault();
    }

    private int AddListMembers(System.Collections.Generic.IEnumerable<AccountContact>? members)
    {
        var added = 0;
        if (members is null)
            return added;

        foreach (var member in members)
        {
            if (member is null || string.IsNullOrWhiteSpace(member.Address))
                continue;
            if (ViewModel.TryAddRecipient(member))
                added++;
        }

        return added;
    }

    private void RemoveByReference(AccountContact recipient)
    {
        for (var index = ViewModel.TemplateToItems.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(ViewModel.TemplateToItems[index], recipient))
            {
                ViewModel.TemplateToItems.RemoveAt(index);
                return;
            }
        }
    }

    private void ClearTypedText(TokenizingTextBox box)
        => DispatcherQueue.TryEnqueue(() => box.Text = string.Empty);

    private void ClearSuggestions(AutoSuggestBox? senderBox = null)
    {
        _templateToSuggestions.Clear();
        if (senderBox is not null)
            senderBox.ItemsSource = null;
    }
}
