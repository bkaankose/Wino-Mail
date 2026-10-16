using System.Globalization;
using System.Runtime.CompilerServices;
using Foundation;
using UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// How a settings card renders as a row on an iOS settings page (design decision 15 Oct 2026, SettingsControls board):
/// the framework picks the kind from the card's content, so pages keep declaring their cards in Windows order.
/// </summary>
public enum WinoSettingsRowKind
{
    /// <summary>Title only (and a subtitle for information rows); no control.</summary>
    Static,
    /// <summary>Title and a trailing native switch; no push.</summary>
    Switch,
    /// <summary>Title, the current value and a chevron; pushes the detail page with the real control.</summary>
    Value,
    /// <summary>Title and a chevron; pushes the detail page (custom content, expanders, navigation cards).</summary>
    Push,
    /// <summary>Accent title that runs the card's button (red when destructive).</summary>
    Action,
    /// <summary>Small trailing content kept on the row: captions, icon buttons, menus, progress.</summary>
    Inline
}

/// <summary>
/// Reads a card's content: which row kind it makes, the value text a value row shows, and the options of a choice
/// control (a pull-down menu button or a segmented control) with a way to select one through the control's own binding.
/// </summary>
public static class WinoSettingsRowContent
{
    /// <summary>Tag on the binder's check buttons, which stay on the row like a switch.</summary>
    public const int CheckButtonTag = 0x5E7C4E;

    private static readonly ConditionalWeakTable<UIButton, object> Destructive = new();

    /// <summary>Marks a button as destructive: its action row is drawn in red.</summary>
    public static T MarkDestructive<T>(T button) where T : UIButton
    {
        Destructive.AddOrUpdate(button, true);
        return button;
    }

    public static bool IsDestructive(UIButton button)
    {
        if (Destructive.TryGetValue(button, out _)) return true;
        var color = button.Configuration?.BaseForegroundColor;
        return color is not null && color.Equals(UIColor.SystemRed);
    }

    public static WinoSettingsRowKind Classify(UIView? content, UIView? bottomContent)
    {
        if (bottomContent is not null) return WinoSettingsRowKind.Push;
        if (content is null) return WinoSettingsRowKind.Static;
        var controls = Controls(content);
        if (controls.Count == 0) return content is UITextView ? WinoSettingsRowKind.Push : WinoSettingsRowKind.Inline;
        if (controls.Count == 1)
        {
            return controls[0] switch
            {
                UISwitch or WinoLabeledSwitch => WinoSettingsRowKind.Switch,
                UIButton button when button.Tag == CheckButtonTag => WinoSettingsRowKind.Inline,
                UIButton button when IsChoice(button) => WinoSettingsRowKind.Value,
                UIButton button when HasTitle(button) && button.Menu is null => WinoSettingsRowKind.Action,
                UIButton => WinoSettingsRowKind.Inline,
                UISegmentedControl or UITextField or UISlider or UIDatePicker or UIStepper => WinoSettingsRowKind.Value,
                UITextView => WinoSettingsRowKind.Push,
                _ => WinoSettingsRowKind.Push
            };
        }
        if (Stepper(content) is not null) return WinoSettingsRowKind.Value;
        // Icon buttons side by side (edit, delete), with or without a switch, stay on the row (data rows).
        if (controls.All(control => control is UISwitch || control is UIButton button && !HasTitle(button) && !IsChoice(button))) return WinoSettingsRowKind.Inline;
        return WinoSettingsRowKind.Push;
    }

    /// <summary>The value control of a value row (popup, segmented control, field, slider, picker, stepper).</summary>
    public static UIView? ValueControl(UIView content)
    {
        var controls = Controls(content);
        if (Stepper(content) is { } stepper) return stepper;
        return controls.Count == 1 ? controls[0] : null;
    }

    /// <summary>The binder's numeric field and stepper pair, if the content is one.</summary>
    public static UIStepper? Stepper(UIView content)
    {
        var controls = Controls(content);
        if (controls.Count is < 1 or > 2) return null;
        var stepper = controls.OfType<UIStepper>().FirstOrDefault();
        if (stepper is null) return null;
        return controls.All(control => control is UIStepper or UITextField) ? stepper : null;
    }

    /// <summary>True for a choice: a titled pull-down menu button or a segmented control.</summary>
    public static bool IsChoiceControl(UIView? control) => control is UISegmentedControl || control is UIButton button && IsChoice(button);

    public static bool IsChoice(UIButton button)
        => button.Menu is not null && button.ShowsMenuAsPrimaryAction && button.Tag != CheckButtonTag
           && (button.Configuration?.Indicator == UIButtonConfigurationIndicator.Popup || button.ChangesSelectionAsPrimaryAction || HasTitle(button));

    public static bool HasTitle(UIButton button) => !string.IsNullOrEmpty(button.Configuration?.Title ?? button.CurrentTitle);

    public static string ButtonTitle(UIButton button) => button.Configuration?.Title ?? button.CurrentTitle ?? string.Empty;

    /// <summary>
    /// The interactive controls of a content tree: every <see cref="UIControl"/> (not descended into), labelled switches
    /// and text views. Hidden controls count too, so a row keeps its kind when a control hides.
    /// </summary>
    public static List<UIView> Controls(UIView content)
    {
        var result = new List<UIView>();
        Collect(content, result);
        return result;
    }

    private static void Collect(UIView view, List<UIView> result)
    {
        switch (view)
        {
            case WinoLabeledSwitch toggle: result.Add(toggle.Switch); return;
            case WinoColorSwatchPicker swatches: result.Add(swatches); return;
            case UIControl control: result.Add(control); return;
            case UITextView text when text.Editable: result.Add(text); return;
        }
        foreach (var child in view.Subviews) Collect(child, result);
    }

    /// <summary>The text a value row shows for <paramref name="content"/>.</summary>
    public static string ValueText(UIView content)
    {
        var control = ValueControl(content);
        switch (control)
        {
            case UIButton button:
                return ButtonTitle(button);
            case UISegmentedControl segmented:
                return segmented.SelectedSegment >= 0 ? segmented.TitleAt(segmented.SelectedSegment) ?? string.Empty : string.Empty;
            case UIStepper stepper:
            {
                var field = Controls(content).OfType<UITextField>().FirstOrDefault();
                var number = field?.Text is { Length: > 0 } text ? text : stepper.Value.ToString(CultureInfo.CurrentCulture);
                return Join(number, Captions(content));
            }
            case UITextField field:
                return field.SecureTextEntry ? (string.IsNullOrEmpty(field.Text) ? string.Empty : "••••") : field.Text ?? string.Empty;
            case UISlider slider:
                return Captions(content) is { Length: > 0 } caption ? caption : Math.Round(slider.Value).ToString(CultureInfo.CurrentCulture);
            case UIDatePicker picker:
                return FormatDate(picker);
        }
        return content is UILabel label ? label.Text ?? string.Empty : string.Empty;
    }

    private static string Join(string value, string caption) => string.IsNullOrEmpty(caption) ? value : $"{value} {caption}";

    /// <summary>Visible labels beside the control (a unit suffix or a slider's value), joined.</summary>
    private static string Captions(UIView content)
    {
        var labels = new List<string>();
        void Walk(UIView view)
        {
            if (view is UIControl) return;
            if (view is UILabel { Hidden: false } label && !string.IsNullOrWhiteSpace(label.Text)) labels.Add(label.Text!);
            foreach (var child in view.Subviews) Walk(child);
        }
        Walk(content);
        return string.Join(" ", labels);
    }

    private static string FormatDate(UIDatePicker picker)
    {
        using var formatter = new NSDateFormatter
        {
            TimeStyle = picker.Mode is UIDatePickerMode.Date ? NSDateFormatterStyle.None : NSDateFormatterStyle.Short,
            DateStyle = picker.Mode is UIDatePickerMode.Time or UIDatePickerMode.CountDownTimer ? NSDateFormatterStyle.None : NSDateFormatterStyle.Medium
        };
        return formatter.ToString(picker.Date);
    }

    /// <summary>The display texts of a choice control.</summary>
    public static IReadOnlyList<string> Options(UIView control) => control switch
    {
        UISegmentedControl segmented => Enumerable.Range(0, (int)segmented.NumberOfSegments).Select(index => segmented.TitleAt(index) ?? string.Empty).ToList(),
        UIButton { Menu: { } menu } => Actions(menu).Select(action => action.Title ?? string.Empty).ToList(),
        _ => []
    };

    /// <summary>The selected option of a choice control, or -1.</summary>
    public static int SelectedIndex(UIView control)
    {
        switch (control)
        {
            case UISegmentedControl segmented:
                return (int)segmented.SelectedSegment;
            case UIButton { Menu: { } menu } button:
                var actions = Actions(menu);
                var index = actions.FindIndex(action => action.State == UIMenuElementState.On);
                return index >= 0 ? index : actions.FindIndex(action => action.Title == ButtonTitle(button));
        }
        return -1;
    }

    /// <summary>Selects an option through the control's own handlers, so it writes back through the same binding.</summary>
    public static void Select(UIView control, int index)
    {
        switch (control)
        {
            case UISegmentedControl segmented when index >= 0 && index < segmented.NumberOfSegments:
                segmented.SelectedSegment = index;
                segmented.SendActionForControlEvents(UIControlEvent.ValueChanged);
                break;
            case UIButton { Menu: { } menu } button:
                var actions = Actions(menu);
                if (index >= 0 && index < actions.Count) Perform(actions[index], button);
                break;
        }
    }

    public static bool IsEnabled(UIView control, int index) => control switch
    {
        UISegmentedControl segmented => segmented.IsEnabled(index),
        UIButton { Menu: { } menu } => Actions(menu) is var actions && index < actions.Count && !actions[index].Attributes.HasFlag(UIMenuElementAttributes.Disabled),
        _ => true
    };

    private static readonly ObjCRuntime.Selector PerformSelector = new("performWithSender:target:");

    /// <summary>Runs a menu action's handler as if it was chosen from the menu (UIAction performWithSender:target:, iOS 17).</summary>
    private static void Perform(UIAction action, UIButton sender)
    {
        if (!action.RespondsToSelector(PerformSelector)) return;
        SendPerform(action.Handle, PerformSelector.Handle, sender.Handle, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPerform(IntPtr receiver, IntPtr selector, IntPtr sender, IntPtr target);

    private static List<UIAction> Actions(UIMenu menu)
    {
        var result = new List<UIAction>();
        foreach (var child in menu.Children)
        {
            if (child is UIAction action) result.Add(action);
            else if (child is UIMenu submenu) result.AddRange(Actions(submenu));
        }
        return result;
    }
}
