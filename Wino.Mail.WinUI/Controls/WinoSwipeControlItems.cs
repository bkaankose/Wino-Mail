using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Helpers;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Mail.WinUI;

namespace Wino.Controls;

public partial class WinoSwipeControl : SwipeControl
{
    private readonly SwipeItems _leftItems = new();
    private readonly SwipeItems _rightItems = new();
    private SwipeItem? _leftSwipeItem;
    private SwipeItem? _rightSwipeItem;
    private bool _isLoaded;

    [GeneratedDependencyProperty]
    public partial MailListRow? MailRow { get; set; }

    [GeneratedDependencyProperty(DefaultValue = SwipeMode.Execute)]
    public partial SwipeMode LeftItemsMode { get; set; }

    [GeneratedDependencyProperty(DefaultValue = SwipeMode.Execute)]
    public partial SwipeMode RightItemsMode { get; set; }

    [GeneratedDependencyProperty]
    public partial Brush? DeleteOperationBrush { get; set; }

    public WinoSwipeControl()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    partial void OnMailRowPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_isLoaded)
        {
            BuildSwipeItems();
        }
    }

    partial void OnLeftItemsModeChanged(SwipeMode newValue)
        => _leftItems.Mode = newValue;

    partial void OnRightItemsModeChanged(SwipeMode newValue)
        => _rightItems.Mode = newValue;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;

        BuildSwipeItems();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        DetachSwipeItems();
        ClearSwipeItems();
    }

    private void BuildSwipeItems()
    {
        DetachSwipeItems();
        ClearSwipeItems();

        if (GetMailItems().Count == 0)
        {
            return;
        }

        var preferencesService = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();

        _leftItems.Mode = LeftItemsMode;
        _rightItems.Mode = RightItemsMode;

        _leftSwipeItem = GetSwipeItem(preferencesService.LeftSwipeOperation);
        if (_leftSwipeItem != null)
        {
            _leftItems.Add(_leftSwipeItem);
        }

        _rightSwipeItem = GetSwipeItem(preferencesService.RightSwipeOperation);
        if (_rightSwipeItem != null)
        {
            _rightItems.Add(_rightSwipeItem);
        }


        AttachSwipeItems();
    }

    private void AttachSwipeItems()
    {
        LeftItems = _leftItems;
        RightItems = _rightItems;
    }

    private void DetachSwipeItems()
    {
        LeftItems = null;
        RightItems = null;
    }

    private void ClearSwipeItems()
    {
        _leftSwipeItem?.Invoked -= SwipeItemInvoked;
        _leftSwipeItem = null;

        _rightSwipeItem?.Invoked -= SwipeItemInvoked;
        _rightSwipeItem = null;

        _leftItems.Clear();
        _rightItems.Clear();
    }

    private SwipeItem? GetSwipeItem(MailOperation operation)
    {
        var mailItems = GetMailItems();
        if (mailItems.Count == 0)
        {
            return null;
        }

        var finalOperation = ResolveFinalOperation(operation, mailItems);

        var item = new SwipeItem()
        {
            IconSource = new WinoFontIconSource() { Icon = XamlHelpers.GetWinoIconGlyph(finalOperation) },
            Text = XamlHelpers.GetOperationString(finalOperation),
            BehaviorOnInvoked = SwipeBehaviorOnInvoked.Close,
            Background = IsDeleteOperation(finalOperation) ? DeleteOperationBrush : null,
            CommandParameter = operation
        };

        item.Invoked += SwipeItemInvoked;

        return item;
    }

    private IReadOnlyList<MailItemViewModel> GetMailItems()
    {
        if (MailRow is { IsThreadHead: true, Thread: { } thread })
        {
            return thread.Items.OfType<MailItemViewModel>().ToArray();
        }

        return MailRow?.SourceItem is MailItemViewModel mailItem
            ? new[] { mailItem }
            : Array.Empty<MailItemViewModel>();
    }

    private static MailOperation ResolveFinalOperation(
        MailOperation operation,
        IReadOnlyList<MailItemViewModel> mailItems)
    {
        if (operation == MailOperation.MarkAsRead &&
            mailItems.All(static item => item.IsRead))
        {
            return MailOperation.MarkAsUnread;
        }

        if (operation == MailOperation.MarkAsUnread &&
            mailItems.All(static item => !item.IsRead))
        {
            return MailOperation.MarkAsRead;
        }

        return operation;
    }

    private static bool IsDeleteOperation(MailOperation operation)
        => operation is MailOperation.SoftDelete or MailOperation.HardDelete;

    private void SwipeItemInvoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        var mailItems = GetMailItems();
        if (mailItems.Count == 0)
        {
            return;
        }

        var operation = (MailOperation)sender.CommandParameter;
        var finalOperation = ResolveFinalOperation(operation, mailItems);

        WeakReferenceMessenger.Default.Send(new SwipeActionRequested(finalOperation, mailItems));
    }
}
