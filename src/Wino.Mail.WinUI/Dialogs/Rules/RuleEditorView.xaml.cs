using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Dialogs.Rules;

/// <summary>
/// The rule editor as a view, so it can live inside the rules manager (ContentDialogs cannot stack,
/// and closing the manager to show a second dialog made every edit flicker) or stand alone in
/// <see cref="RuleEditorDialog"/> for the "create rule from message" path.
///
/// The view does not persist anything. It raises <see cref="SaveRequested"/> with the edited rule,
/// <see cref="DeleteRequested"/> or <see cref="Cancelled"/>; the host decides what to do, and can keep
/// the view open with <see cref="SetBusy"/> / <see cref="ShowError"/> while a save is in flight.
/// </summary>
public sealed partial class RuleEditorView : UserControl
{
    private RemoteInboxRule? _rule;
    private IReadOnlyList<MailItemFolder> _folders = Array.Empty<MailItemFolder>();
    private IReadOnlyList<MailCategory> _categories = Array.Empty<MailCategory>();

    public ObservableCollection<RuleConditionRow> Conditions { get; } = [];
    public ObservableCollection<RuleActionRow> Actions { get; } = [];

    public event EventHandler<RemoteInboxRule>? SaveRequested;
    public event EventHandler<RemoteInboxRule>? DeleteRequested;
    public event EventHandler? Cancelled;

    public RuleEditorView()
    {
        InitializeComponent();
        ConditionsList.ItemsSource = Conditions;
        ActionsList.ItemsSource = Actions;
    }

    /// <param name="rule">The rule to edit, or null for a new rule.</param>
    public void Load(RemoteInboxRule? rule, IReadOnlyList<MailItemFolder>? folders, IReadOnlyList<MailCategory>? categories)
    {
        _rule = rule;
        _folders = folders ?? Array.Empty<MailItemFolder>();
        _categories = categories ?? Array.Empty<MailCategory>();

        var isNew = rule == null || string.IsNullOrEmpty(rule.Id);
        EditorTitleText.Text = isNew ? Translator.Rules_NewRuleTitle : Translator.Rules_EditRuleTitle;
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

        RuleNameTextBox.Text = rule?.Name ?? string.Empty;
        EnabledSwitch.IsOn = rule?.IsEnabled ?? true;
        StopProcessingCheck.IsChecked = rule?.StopProcessing ?? false;

        // Seed rows from the rule, or one default of each for a new rule.
        Conditions.Clear();
        if (rule?.Conditions?.Count > 0)
            foreach (var c in rule.Conditions)
                Conditions.Add(new RuleConditionRow(_folders, _categories, c.Field, c.Value));
        else
            Conditions.Add(new RuleConditionRow(_folders, _categories));

        Actions.Clear();
        if (rule?.Actions?.Count > 0)
            foreach (var a in rule.Actions)
                Actions.Add(new RuleActionRow(_folders, _categories, a.Type, a.Value));
        else
            Actions.Add(new RuleActionRow(_folders, _categories));

        ClearError();
        SetBusy(false);
        Validate();
    }

    public void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        SaveButton.IsEnabled = !busy && IsValid();
        CancelButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    public void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        Conditions.Add(new RuleConditionRow(_folders, _categories, RuleConditionField.SubjectContains));
        Validate();
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        Actions.Add(new RuleActionRow(_folders, _categories, RuleActionType.Categorize));
        Validate();
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleConditionRow row })
            Conditions.Remove(row);
        Validate();
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleActionRow row })
            Actions.Remove(row);
        Validate();
    }

    private void RuleNameChanged(object sender, TextChangedEventArgs e) => Validate();

    private bool IsValid()
        => !string.IsNullOrWhiteSpace(RuleNameTextBox.Text) && Conditions.Count > 0 && Actions.Count > 0;

    private void Validate() => SaveButton.IsEnabled = !BusyRing.IsActive && IsValid();

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        var result = new RemoteInboxRule
        {
            Id = _rule?.Id,
            Name = RuleNameTextBox.Text.Trim(),
            IsEnabled = EnabledSwitch.IsOn,
            StopProcessing = StopProcessingCheck.IsChecked == true,
            Priority = _rule?.Priority ?? 0,
            Conditions = Conditions.Select(c => new RuleConditionModel(c.Field, c.Value)).ToList(),
            Actions = Actions.Select(a => new RuleActionModel(a.Type, a.Value)).ToList()
        };

        SaveRequested?.Invoke(this, result);
    }

    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_rule?.Id != null)
            DeleteRequested?.Invoke(this, _rule);
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);
}
