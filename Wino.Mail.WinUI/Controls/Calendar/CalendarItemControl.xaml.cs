using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
namespace Wino.Calendar.Controls;

public sealed partial class CalendarItemControl : UserControl
{
    private readonly ICalendarContextMenuItemService _contextMenuItemService;

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
        _contextMenuItemService = WinoApplication.Current.Services.GetRequiredService<ICalendarContextMenuItemService>();
        InitializeComponent();
    }

    private static void OnCalendarItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CalendarItemControl control)
        {
            control.UpdateVisualStates();
        }
    }

    private void UpdateVisualStates()
    {
        CanDrag = CalendarItem?.CanDragDrop == true;

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

        flyout.SetMenuItems(_contextMenuItemService.GetContextMenuItems(CalendarItem.CalendarItem));
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
