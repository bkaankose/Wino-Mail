using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.ModeSwitcher;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class ModeSwitcherPage : Page
{
    public ModeSwitcherPage()
    {
        InitializeComponent();

        LiveSwitcher.SelectedItem = MailItem;
        MailOnlySwitcher.SelectedItem = MailOnlyMailItem;
        DisabledSwitcher.SelectedItem = DisabledMailItem;
    }

    private void SwitcherItemInvoked(object sender, WinoModeSwitcherItemInvokedEventArgs e)
    {
        InvokedText.Text = $"Last invoked: {e.Item.Label} (tag {e.Item.Tag})";
    }

    private void ModeToggled(object sender, RoutedEventArgs e)
    {
        // XAML raises Toggled while assigning the initial IsOn, before the later toggles exist.
        if (LiveSwitcher is null || MailToggle is null || CalendarToggle is null || ContactsToggle is null || ToDoToggle is null)
        {
            return;
        }

        MailItem.IsVisible = MailToggle.IsOn;
        CalendarItem.IsVisible = CalendarToggle.IsOn;
        ContactsItem.IsVisible = ContactsToggle.IsOn;
        ToDoItem.IsVisible = ToDoToggle.IsOn;

        // The control hides the thumb when the selected mode goes away; a host picks the
        // replacement, and here that is the first mode that is still visible.
        if (LiveSwitcher.SelectedItem is { IsVisible: false })
        {
            LiveSwitcher.SelectedItem = LiveSwitcher.Items.FirstOrDefault(item => item.IsVisible);
        }
    }

    private void ApplyPreset(bool mail, bool calendar, bool contacts, bool toDo)
    {
        MailToggle.IsOn = mail;
        CalendarToggle.IsOn = calendar;
        ContactsToggle.IsOn = contacts;
        ToDoToggle.IsOn = toDo;
    }

    private void PresetAllClicked(object sender, RoutedEventArgs e) => ApplyPreset(true, true, true, true);

    private void PresetMailClicked(object sender, RoutedEventArgs e) => ApplyPreset(true, false, false, false);

    private void PresetMailCalendarClicked(object sender, RoutedEventArgs e) => ApplyPreset(true, true, false, false);

    private void PresetNoneClicked(object sender, RoutedEventArgs e) => ApplyPreset(false, false, false, false);

    private void CompactToggled(object sender, RoutedEventArgs e)
    {
        if (LiveSwitcher is null)
        {
            return;
        }

        var isCompact = CompactToggle.IsOn;

        LivePane.Width = isCompact ? 48 : 280;
        PaneCaption.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        LiveSwitcher.Orientation = isCompact ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void EnabledToggled(object sender, RoutedEventArgs e)
    {
        if (LiveSwitcher is null)
        {
            return;
        }

        LiveSwitcher.IsEnabled = EnabledToggle.IsOn;
    }

    private void LightThemeToggled(object sender, RoutedEventArgs e)
    {
        if (LivePane is null)
        {
            return;
        }

        // Only the sample pane flips, so both palettes can be compared without leaving the page.
        LivePane.RequestedTheme = LightThemeToggle.IsOn ? ElementTheme.Light : ElementTheme.Default;
    }

    private void SelectCalendarClicked(object sender, RoutedEventArgs e) => LiveSwitcher.SelectedItem = CalendarItem;

    private void ClearSelectionClicked(object sender, RoutedEventArgs e) => LiveSwitcher.SelectedItem = null;
}
