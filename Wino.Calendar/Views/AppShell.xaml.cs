using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Views.Abstract;
using Wino.Core.UWP;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views;

public sealed partial class AppShell : AppShellAbstract,
    IRecipient<CalendarDisplayTypeChangedMessage>
{
    private const string STATE_HorizontalCalendar = "HorizontalCalendar";
    private const string STATE_VerticalCalendar = "VerticalCalendar";

    public Frame GetShellFrame() => ShellFrame;

    public AppShell()
    {
        InitializeComponent();

        Window.Current.SetTitleBar(DragArea);
        ManageCalendarDisplayType(ViewModel.StatePersistenceService.CalendarDisplayType);
    }

    private void ManageCalendarDisplayType(Core.Domain.Enums.CalendarDisplayType displayType)
    {
        // Go to different states based on the display type.
        if (displayType == Core.Domain.Enums.CalendarDisplayType.Month)
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
        ManageCalendarDisplayType(message.NewDisplayType);
    }

    private void ShellFrameContentNavigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        => RealAppBar.ShellFrameContent = (e.Content as BasePage).ShellContent;

    private void AppBarBackButtonClicked(Core.UWP.Controls.WinoAppTitleBar sender, RoutedEventArgs args)
        => ViewModel.NavigationService.GoBack();

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
