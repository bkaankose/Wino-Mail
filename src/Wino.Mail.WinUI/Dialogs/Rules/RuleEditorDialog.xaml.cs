using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Dialogs.Rules;

/// <summary>
/// Stand-alone host for <see cref="RuleEditorView"/>: the "create rule from message" path, where no
/// rules manager is open. The manager hosts the view itself.
/// </summary>
public sealed partial class RuleEditorDialog : ContentDialog
{
    public bool Saved { get; private set; }
    public bool DeleteRequested { get; private set; }
    public RemoteInboxRule? ResultRule { get; private set; }

    /// <param name="rule">The rule to edit, or null for a new rule.</param>
    public RuleEditorDialog(RemoteInboxRule? rule, IReadOnlyList<MailItemFolder> folders, IReadOnlyList<MailCategory> categories)
    {
        InitializeComponent();
        Editor.Load(rule, folders, categories);
    }

    private void Editor_SaveRequested(object? sender, RemoteInboxRule rule)
    {
        ResultRule = rule;
        Saved = true;
        Hide();
    }

    private void Editor_DeleteRequested(object? sender, RemoteInboxRule rule)
    {
        DeleteRequested = true;
        Hide();
    }

    private void Editor_Cancelled(object? sender, EventArgs e) => Hide();
}
