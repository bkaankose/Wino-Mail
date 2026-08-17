using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Dialogs;

/// <summary>
/// Edits one coverage rule. The rule editor lives here rather than on the page because it is a
/// small form: nested inside a settings expander it never had room for a period control, a count
/// control and a live result at once.
/// </summary>
public sealed partial class IntelligenceCoverageRuleDialog : ContentDialog
{
    public IntelligenceCoverageRuleEditor Editor { get; }

    public IntelligenceCoverageRuleDialog(IntelligenceCoverageRuleEditor editor)
    {
        Editor = editor;
        InitializeComponent();
    }
}
