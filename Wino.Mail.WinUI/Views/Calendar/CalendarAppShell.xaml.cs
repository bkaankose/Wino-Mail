using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Views.Abstract;
using Wino.Messaging.Client.Calendar;

namespace Wino.Mail.WinUI.Views.Calendar;

public sealed partial class CalendarAppShell : CalendarAppShellAbstract,
    IRecipient<CalendarDisplayTypeChangedMessage>
{
    private const string STATE_HorizontalCalendar = "HorizontalCalendar";
    private const string STATE_VerticalCalendar = "VerticalCalendar";

    public Frame GetShellFrame() => InnerShellFrame;

    public CalendarAppShell()
    {
        InitializeComponent();

        // Window.Current.SetTitleBar(DragArea);
        ManageCalendarDisplayType();
    }

    private void ManageCalendarDisplayType()
    {
        // Go to different states based on the display type.
        if (ViewModel.IsVerticalCalendar)
        {
            VisualStateManager.GoToState(this, STATE_VerticalCalendar, false);
        }
        else
        {
            VisualStateManager.GoToState(this, STATE_HorizontalCalendar, false);
        }
    }

    private void PreviousDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoPreviousDateRequestedMessage());

    private void NextDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoNextDateRequestedMessage());

    public void Receive(CalendarDisplayTypeChangedMessage message)
    {
        ManageCalendarDisplayType();
    }

    //private void ShellFrameContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    //    => RealAppBar.ShellFrameContent = (e.Content as BasePage).ShellContent;

    //private void AppBarBackButtonClicked(Core.UWP.Controls.WinoAppTitleBar sender, RoutedEventArgs args)
    //    => ViewModel.NavigationService.GoBack();

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<CalendarDisplayTypeChangedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<CalendarDisplayTypeChangedMessage>(this);
    }
}
