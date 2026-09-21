using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Dialogs.Rules;

/// <summary>
/// The "Run rules now" confirmation: what will be applied to how many messages, plus the notes about
/// matches that only run on the server and read-only rules that were left out.
/// </summary>
public sealed partial class RunRulesNowConfirmDialog : ContentDialog
{
    public string Header { get; }
    public IReadOnlyList<string> SummaryLines { get; }
    public string SkippedNote { get; }
    public string ReadOnlyNote { get; }

    public bool IsSkippedNoteEmpty => string.IsNullOrEmpty(SkippedNote);
    public bool IsReadOnlyNoteEmpty => string.IsNullOrEmpty(ReadOnlyNote);

    public RunRulesNowConfirmDialog(string header, IReadOnlyList<string> summaryLines, string skippedNote, string readOnlyNote)
    {
        Header = header;
        SummaryLines = summaryLines;
        SkippedNote = skippedNote;
        ReadOnlyNote = readOnlyNote;

        InitializeComponent();
    }
}
