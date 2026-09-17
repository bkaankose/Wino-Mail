using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Dialogs.Rules;

public sealed partial class RulesManagerDialog : ContentDialog
{
    private readonly MailAccount _account;
    private readonly IRuleService _ruleService;
    private readonly IReadOnlyList<MailItemFolder> _folders;
    private readonly IReadOnlyList<MailCategory> _categories;

    private readonly ObservableCollection<RuleListItem> _items = [];

    // The failed mutation to re-run with removeOutlookRuleBlob=true when the user consents.
    private Func<bool, Task<InboxRuleUpdateResult>>? _pendingBlobRetry;

    // Hand-off flag read by the orchestrator after ShowAsync (run-now needs its own dialogs).
    public bool RunNowRequested { get; private set; }

    public RulesManagerDialog(MailAccount account, IRuleService ruleService,
        IReadOnlyList<MailItemFolder>? folders, IReadOnlyList<MailCategory>? categories)
    {
        InitializeComponent();

        _account = account;
        _ruleService = ruleService;
        _folders = folders ?? Array.Empty<MailItemFolder>();
        _categories = categories ?? Array.Empty<MailCategory>();

        RulesListView.ItemsSource = _items;
    }

    private async void Dialog_Loaded(object sender, RoutedEventArgs e) => await LoadRulesAsync();

    private async Task LoadRulesAsync()
    {
        SetBusy(true);
        ClearError();
        try
        {
            var rules = await _ruleService.GetRulesAsync(_account.Id);

            _items.Clear();
            foreach (var rule in rules)
                _items.Add(new RuleListItem(rule, RuleUiCatalog.BuildSummary(rule, _folders, _categories)));

            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionState();
        }
        catch (Exception ex)
        {
            ShowError(string.Format(Translator.Rules_LoadFailed, ex.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private RuleListItem? Selected => RulesListView.SelectedItem as RuleListItem;

    private void RulesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectionState();

    private void UpdateSelectionState()
    {
        var sel = Selected;
        var index = sel == null ? -1 : _items.IndexOf(sel);

        ChangeButton.IsEnabled = sel != null && !sel.IsReadOnly;
        CopyButton.IsEnabled = sel != null && !sel.IsReadOnly;
        DeleteButton.IsEnabled = sel != null;
        MoveUpButton.IsEnabled = sel != null && index > 0;
        MoveDownButton.IsEnabled = sel != null && index >= 0 && index < _items.Count - 1;

        DescriptionText.Text = sel?.Summary ?? Translator.Rules_SelectRuleHint;
    }

    // ---- Editing in place: the editor view replaces the list inside this dialog -----------------

    private void NewRuleClicked(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void ChangeRuleClicked(object sender, RoutedEventArgs e) => RequestEdit(Selected);

    private void RulesListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => RequestEdit(Selected);

    private void RequestEdit(RuleListItem? item)
    {
        if (item == null || item.IsReadOnly)
            return;

        OpenEditor(item.Rule);
    }

    private void OpenEditor(RemoteInboxRule? rule)
    {
        ClearError();
        EditorPane.Load(rule, _folders, _categories);
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
    }

    private void CloseEditor()
    {
        EditorPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
    }

    private void Editor_Cancelled(object? sender, EventArgs e) => CloseEditor();

    private async void Editor_SaveRequested(object? sender, RemoteInboxRule rule)
    {
        // A new rule appends to the end of the priority order.
        if (string.IsNullOrEmpty(rule.Id))
            rule.Priority = (_items.Count == 0 ? 0 : _items.Max(i => i.Rule.Priority)) + 1;

        await RunEditorMutationAsync(removeBlob => _ruleService.SaveRuleAsync(_account.Id, rule, removeBlob));
    }

    private async void Editor_DeleteRequested(object? sender, RemoteInboxRule rule)
        => await RunEditorMutationAsync(removeBlob => _ruleService.DeleteRuleAsync(_account.Id, rule.Id, removeBlob));

    // Runs a mutation from the editor: on success the editor closes and the list reloads in place; on
    // failure the editor stays open with the error (the Outlook-blob consent button lives on the list
    // pane, so that case returns there with the retry armed).
    private async Task RunEditorMutationAsync(Func<bool, Task<InboxRuleUpdateResult>> operation)
    {
        EditorPane.SetBusy(true);
        EditorPane.ClearError();
        try
        {
            var result = await operation(false);
            if (result.Success)
            {
                CloseEditor();
                await LoadRulesAsync();
                return;
            }

            if (result.RequiresOutlookRuleBlobRemoval)
            {
                CloseEditor();
                ReportFailure(result, operation);
                return;
            }

            EditorPane.ShowError(JoinErrors(result));
        }
        catch (Exception ex)
        {
            EditorPane.ShowError(ex.Message);
        }
        finally
        {
            EditorPane.SetBusy(false);
        }
    }

    private void RunRulesNowClicked(object sender, RoutedEventArgs e)
    {
        RunNowRequested = true;
        Hide();
    }

    private void DoneClicked(object sender, RoutedEventArgs e) => Hide();

    // ---- In-place mutations (persist to server, then reload) -----------------------------------

    private async void RuleEnabledToggled(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleListItem item || item.IsReadOnly)
            return;

        ClearError();
        var intendedState = item.IsEnabled;
        var result = await _ruleService.SaveRuleAsync(_account.Id, item.Rule);
        if (!result.Success)
        {
            // Revert the optimistic toggle and report; the blob-retry re-applies the intended state.
            item.IsEnabled = !intendedState;
            ReportFailure(result, async removeBlob =>
            {
                item.IsEnabled = intendedState;
                var retry = await _ruleService.SaveRuleAsync(_account.Id, item.Rule, removeBlob);
                if (!retry.Success)
                    item.IsEnabled = !intendedState;
                return retry;
            });
        }
    }

    private async void CopyRuleClicked(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel == null || sel.IsReadOnly)
            return;

        var copy = CloneRule(sel.Rule);
        await RunMutationAsync(removeBlob => _ruleService.SaveRuleAsync(_account.Id, copy, removeBlob));
    }

    private async void DeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel?.Rule?.Id == null)
            return;

        await RunMutationAsync(removeBlob => _ruleService.DeleteRuleAsync(_account.Id, sel.Rule.Id, removeBlob));
    }

    private async void MoveUpClicked(object sender, RoutedEventArgs e) => await MoveSelectedAsync(-1);

    private async void MoveDownClicked(object sender, RoutedEventArgs e) => await MoveSelectedAsync(1);

    private async Task MoveSelectedAsync(int direction)
    {
        var sel = Selected;
        if (sel == null)
            return;

        var i = _items.IndexOf(sel);
        var j = i + direction;
        if (i < 0 || j < 0 || j >= _items.Count)
            return;

        _items.Move(i, j);
        var ordered = _items.Select(it => it.Rule).ToList();
        await RunMutationAsync(removeBlob => _ruleService.ReorderRulesAsync(_account.Id, ordered, removeBlob));
    }

    // The operation takes removeOutlookRuleBlob: it runs normally first, and when the server reports the
    // classic-Outlook rule blob blocks the update, the same operation is re-run with true after the user
    // clicks the consent button revealed under the error text.
    private Task RunMutationAsync(Func<bool, Task<InboxRuleUpdateResult>> operation)
        => RunListMutationAsync(() => operation(false), result => ReportFailure(result, operation));

    // Runs one list mutation against the server: busy ring up, error/consent UI cleared, and the list
    // reloaded only on success, since reloading would clear the error a failure has just surfaced.
    private async Task RunListMutationAsync(Func<Task<InboxRuleUpdateResult>> operation, Action<InboxRuleUpdateResult> onFailure)
    {
        SetBusy(true);
        ClearError();
        var succeeded = false;
        try
        {
            var result = await operation();
            succeeded = result.Success;
            if (!succeeded)
                onFailure(result);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        if (succeeded)
            await LoadRulesAsync();
    }

    private void ReportFailure(InboxRuleUpdateResult result, Func<bool, Task<InboxRuleUpdateResult>> operation)
    {
        ShowError(JoinErrors(result));

        if (result.RequiresOutlookRuleBlobRemoval)
        {
            _pendingBlobRetry = operation;
            RemoveBlobRetryButton.Visibility = Visibility.Visible;
        }
    }

    private async void RemoveBlobRetryClicked(object sender, RoutedEventArgs e)
    {
        var operation = _pendingBlobRetry;
        if (operation == null)
            return;

        // The consent has been given once: a failure here reports the error without offering it again.
        await RunListMutationAsync(() => operation(true), result => ShowError(JoinErrors(result)));
    }

    private static RemoteInboxRule CloneRule(RemoteInboxRule rule) => new()
    {
        Id = null,
        Name = rule.Name + " (copy)",
        Priority = rule.Priority,
        IsEnabled = rule.IsEnabled,
        StopProcessing = rule.StopProcessing,
        Conditions = rule.Conditions.Select(c => new RuleConditionModel(c.Field, c.Value)).ToList(),
        Actions = rule.Actions.Select(a => new RuleActionModel(a.Type, a.Value)).ToList()
    };

    // ---- helpers -------------------------------------------------------------------------------

    private void SetBusy(bool busy) => BusyRing.IsActive = busy;

    private static string JoinErrors(InboxRuleUpdateResult result)
    {
        var detail = result.Errors.Count > 0 ? string.Join(" ", result.Errors) : "unknown error";
        return string.Format(Translator.Rules_SaveFailed, detail);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        RemoveBlobRetryButton.Visibility = Visibility.Collapsed;
        _pendingBlobRetry = null;
    }
}
