#if DEBUG
using System.Text;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

// Debug drivers: they set the page's own drop-downs and entries, so the widget -> ViewModel path is what runs.
internal sealed partial class MailFilterEditorPage
{
    internal string DebugDescribe()
    {
        var vm = ViewModel;
        var text = new StringBuilder();
        text.AppendLine($"title={PageTitle} name={vm.FilterName} management={vm.SelectedManagementType?.Value} canChange={vm.CanChangeManagementType} managementSensitive={_management?.Root.GetSensitive()}");
        text.AppendLine($"source={vm.SelectedSourceFolder?.DisplayName} match={vm.SelectedMatchMode?.Value} enabled={vm.IsEnabled} stop={vm.StopProcessing} canAddAction={vm.CanAddAction} addActionSensitive={_addAction?.GetSensitive()} hint={(_hintRow?.GetVisible() == true ? _hint?.GetText() : string.Empty)}");
        foreach (var condition in _conditions)
        {
            var item = condition.Item;
            text.AppendLine($"condition|{item.ConjunctionText}|show={item.ShowConjunction}|{item.SelectedField?.Value}|{item.SelectedOperator?.Value}|value={item.Value}|choice={item.SelectedChoice?.Value}|text={condition.Value.GetVisible()}|choiceVisible={condition.Choice.GetVisible()}|operators={item.AvailableOperators.Length}|choices={item.ChoiceOptions.Length}");
        }
        foreach (var action in _actions)
            text.AppendLine($"action|{action.Item.SelectedAction?.Value}|folder={action.Item.SelectedTargetFolder?.DisplayName}|folderVisible={action.Folder.GetVisible()}|warning={action.Warning.GetVisible()}");
        text.AppendLine($"summary={vm.Summary}");
        return text.ToString();
    }

    internal bool DebugSet(string kind, string value)
    {
        switch (kind)
        {
            case "name":
                _nameEntry?.SetText(value);
                return _nameEntry is not null;
            case "source":
            {
                var index = ViewModel.Folders.ToList().FindIndex(folder => string.Equals(folder.DisplayName, value, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || _sourceRow is null) return false;
                _sourceRow.SetSelected((uint)index);
                return true;
            }
            case "match":
                _matchDropDown?.SetSelected(string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase) ? 1u : 0u);
                return _matchDropDown is not null;
            default:
                return false;
        }
    }

    internal void DebugCondition(string target, string field, string op, string value)
    {
        if (target == "add")
        {
            ViewModel.AddCondition();
            MainLoop.Delay(200, () => DebugCondition((_conditions.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), field, op, value));
            return;
        }
        if (!int.TryParse(target, out var index) || index < 0 || index >= _conditions.Count) return;
        var controls = _conditions[index];
        var item = controls.Item;
        var fieldIndex = Array.FindIndex(item.FieldOptions, option => option.Value.ToString() == field);
        if (fieldIndex >= 0) controls.Field.SetSelected((uint)fieldIndex);
        var operatorIndex = Array.FindIndex(item.AvailableOperators, option => option.Value.ToString() == op);
        if (operatorIndex >= 0) controls.Operator.SetSelected((uint)operatorIndex);
        if (item.IsTextField) controls.Value.SetText(value);
        else
        {
            var choiceIndex = Array.FindIndex(item.ChoiceOptions, option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.DisplayName, value, StringComparison.OrdinalIgnoreCase));
            if (choiceIndex >= 0) controls.Choice.SetSelected((uint)choiceIndex);
        }
    }

    internal void DebugAction(string target, string type, string folder)
    {
        if (target == "add")
        {
            ViewModel.AddAction();
            MainLoop.Delay(200, () => DebugAction((_actions.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), type, folder));
            return;
        }
        if (!int.TryParse(target, out var index) || index < 0 || index >= _actions.Count) return;
        var controls = _actions[index];
        var actionIndex = Array.FindIndex(controls.Item.ActionOptions, option => option.Value.ToString() == type);
        if (actionIndex >= 0) controls.Action.SetSelected((uint)actionIndex);
        if (folder == "-") return;
        var folderIndex = controls.Item.FolderOptions.ToList().FindIndex(option => string.Equals(option.DisplayName, folder, StringComparison.OrdinalIgnoreCase));
        if (folderIndex >= 0) controls.Folder.SetSelected((uint)folderIndex);
    }

    internal void DebugRemove(string kind, int index)
    {
        if (kind == "condition" && index >= 0 && index < _conditions.Count) ViewModel.RemoveCondition(_conditions[index].Item);
        else if (kind == "action" && index >= 0 && index < _actions.Count) ViewModel.RemoveAction(_actions[index].Item);
    }

    internal void DebugSave() => Observe(ViewModel.SaveAsync());
}
#endif
