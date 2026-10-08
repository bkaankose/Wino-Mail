using System.Threading.Tasks;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
namespace Wino.Calendar.Controls;

public sealed partial class CalendarItemControl : UserControl
{
    [CommunityToolkit.WinUI.GeneratedDependencyProperty]
    public partial bool ProtectTitle { get; set; }

    private readonly IContextMenuItemService _contextMenuItemService;
#if DEBUG
    private readonly INotificationBuilder _notificationBuilder;
#endif

    // Single tap has a delay to report double taps properly.
    private bool isSingleTap = false;

    public static readonly DependencyProperty CalendarItemProperty = DependencyProperty.Register(nameof(CalendarItem), typeof(CalendarItemViewModel), typeof(CalendarItemControl), new PropertyMetadata(null, new PropertyChangedCallback(OnCalendarItemChanged)));
    public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(nameof(IsDragging), typeof(bool), typeof(CalendarItemControl), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCustomEventAreaProperty = DependencyProperty.Register(nameof(IsCustomEventArea), typeof(bool), typeof(CalendarItemControl), new PropertyMetadata(false));

    /// <summary>
    /// Whether the control is displaying as regular event or all-multi day area in the day control.
    /// </summary>
    public bool IsCustomEventArea
    {
        get { return (bool)GetValue(IsCustomEventAreaProperty); }
        set { SetValue(IsCustomEventAreaProperty, value); }
    }

    public CalendarItemViewModel CalendarItem
    {
        get { return (CalendarItemViewModel)GetValue(CalendarItemProperty); }
        set { SetValue(CalendarItemProperty, value); }
    }

    public bool IsDragging
    {
        get { return (bool)GetValue(IsDraggingProperty); }
        set { SetValue(IsDraggingProperty, value); }
    }

    public CalendarItemControl()
    {
        _contextMenuItemService = WinoApplication.Current.Services.GetRequiredService<IContextMenuItemService>();
#if DEBUG
        _notificationBuilder = WinoApplication.Current.Services.GetRequiredService<INotificationBuilder>();
#endif
        InitializeComponent();
    }

    private static void OnCalendarItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CalendarItemControl control)
        {
            if (e.OldValue is CalendarItemViewModel previous) previous.PropertyChanged -= control.OnEventPropertyChanged;
            if (control.IsLoaded && e.NewValue is CalendarItemViewModel current) current.PropertyChanged += control.OnEventPropertyChanged;
            control.UpdateVisualStates();
            FrameworkElementAutomationPeer.FromElement(control)?.InvalidatePeer();
        }
    }

    private void OnControlLoaded(object sender, RoutedEventArgs args)
    {
        if (CalendarItem is null) return;
        CalendarItem.PropertyChanged -= OnEventPropertyChanged;
        CalendarItem.PropertyChanged += OnEventPropertyChanged;
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs args)
    {
        if (CalendarItem is not null) CalendarItem.PropertyChanged -= OnEventPropertyChanged;
    }

    private void OnEventPropertyChanged(object? sender, PropertyChangedEventArgs args)
        => FrameworkElementAutomationPeer.FromElement(this)?.InvalidatePeer();

    private void UpdateVisualStates()
    {
        if (CalendarItem == null) return;

        if (CalendarItem.IsAllDayEvent)
        {
            VisualStateManager.GoToState(this, "AllDayEvent", true);
        }
        else if (CalendarItem.IsMultiDayEvent)
        {
            if (IsCustomEventArea)
            {
                VisualStateManager.GoToState(this, "CustomAreaMultiDayEvent", true);
            }
            else
            {
                // Hide it.
                VisualStateManager.GoToState(this, "MultiDayEvent", true);
            }
        }
        else
        {
            VisualStateManager.GoToState(this, "RegularEvent", true);
        }
    }

    private void ControlDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (CalendarItem?.CanDragDrop != true)
        {
            args.Cancel = true;
            IsDragging = false;
            return;
        }

        args.AllowedOperations = DataPackageOperation.Move;

        var dragPackage = new CalendarDragPackage(CalendarItem);

        args.Data.Properties.Add(nameof(CalendarDragPackage), dragPackage);
        args.Data.SetText(CalendarItem.DisplayTitle);
        args.Data.Properties.Title = CalendarItem.DisplayTitle;
        args.DragUI.SetContentFromDataPackage();
        IsDragging = true;
    }

    private void ControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EventDetailsPanel.Visibility = !IsCustomEventArea && CalendarItem?.IsAllDayEvent == false &&
            CalendarItem?.IsMultiDayEvent == false && e.NewSize.Height >= 52
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ControlDropped(UIElement sender, DropCompletedEventArgs args) => IsDragging = false;

    private async void ControlTapped(object sender, TappedRoutedEventArgs e)
    {
        if (CalendarItem == null) return;

        isSingleTap = true;

        await Task.Delay(100);

        if (isSingleTap && CalendarItem != null)
        {
            WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(CalendarItem));
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CalendarItemControlAutomationPeer(this);

    internal bool IsAccessibleEvent => CalendarItem is not null && (!CalendarItem.IsMultiDayEvent || IsCustomEventArea);

    internal void OpenAccessibleDetails()
    {
        if (!IsAccessibleEvent || CalendarItem.IsBusy) return;
        isSingleTap = false;
        WeakReferenceMessenger.Default.Send(new CalendarItemDoubleTappedMessage(CalendarItem));
    }

    private void ControlDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (CalendarItem == null) return;

        isSingleTap = false;

        WeakReferenceMessenger.Default.Send(new CalendarItemDoubleTappedMessage(CalendarItem));
    }

    private void ControlRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (CalendarItem == null)
            return;

        if (CalendarItem.IsBusy)
        {
            e.Handled = true;
            return;
        }

        WeakReferenceMessenger.Default.Send(new CalendarItemRightTappedMessage(CalendarItem));
    }

    private void CalendarItemCommandBarFlyout_Opening(object sender, object e)
    {
        if (sender is not CalendarItemCommandBarFlyout flyout)
        {
            return;
        }

        flyout.Item = CalendarItem;

        if (CalendarItem?.CalendarItem == null)
        {
            flyout.ClearMenuItems();
            return;
        }

        flyout.SetMenuItems(_contextMenuItemService.GetCalendarItemContextMenuItems(CalendarItem.CalendarItem));
#if DEBUG
        flyout.AddTestNotificationCommand(
            () => _notificationBuilder.CreateTestCalendarReminderNotificationAsync(CalendarItem.CalendarItem));
#endif
    }

    private void CalendarItemCommandBarFlyout_Closed(object sender, object e)
    {
        if (sender is not CalendarItemCommandBarFlyout flyout)
        {
            return;
        }

        flyout.ClearMenuItems();
        flyout.Item = null;
    }
}
