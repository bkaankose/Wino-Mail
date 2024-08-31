using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Serilog;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Extensions;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;

namespace Wino.Controls.Advanced
{
    /// <summary>
    /// Custom ListView control that handles multiple selection with Extended/Multiple selection mode
    /// and supports threads.
    /// </summary>
    public class WinoListView : ListView, IDisposable
    {
        private ILogger logger = Log.ForContext<WinoListView>();

        private const string PART_ScrollViewer = "ScrollViewer";
        private ScrollViewer internalScrollviewer;

        /// <summary>
        /// Gets or sets whether this ListView belongs to thread items.
        /// This is important for detecting selected items etc.
        /// </summary>
        public bool IsThreadListView
        {
            get { return (bool)GetValue(IsThreadListViewProperty); }
            set { SetValue(IsThreadListViewProperty, value); }
        }

        public ICommand ItemDeletedCommand
        {
            get { return (ICommand)GetValue(ItemDeletedCommandProperty); }
            set { SetValue(ItemDeletedCommandProperty, value); }
        }

        public ICommand LoadMoreCommand
        {
            get { return (ICommand)GetValue(LoadMoreCommandProperty); }
            set { SetValue(LoadMoreCommandProperty, value); }
        }

        public bool IsThreadScrollingEnabled
        {
            get { return (bool)GetValue(IsThreadScrollingEnabledProperty); }
            set { SetValue(IsThreadScrollingEnabledProperty, value); }
        }

        public static readonly DependencyProperty IsThreadScrollingEnabledProperty = DependencyProperty.Register(nameof(IsThreadScrollingEnabled), typeof(bool), typeof(WinoListView), new PropertyMetadata(false));
        public static readonly DependencyProperty LoadMoreCommandProperty = DependencyProperty.Register(nameof(LoadMoreCommand), typeof(ICommand), typeof(WinoListView), new PropertyMetadata(null));
        public static readonly DependencyProperty IsThreadListViewProperty = DependencyProperty.Register(nameof(IsThreadListView), typeof(bool), typeof(WinoListView), new PropertyMetadata(false, new PropertyChangedCallback(OnIsThreadViewChanged)));
        public static readonly DependencyProperty ItemDeletedCommandProperty = DependencyProperty.Register(nameof(ItemDeletedCommand), typeof(ICommand), typeof(WinoListView), new PropertyMetadata(null));

        public WinoListView()
        {
            CanDragItems = true;
            IsItemClickEnabled = true;
            IsMultiSelectCheckBoxEnabled = true;
            IsRightTapEnabled = true;
            SelectionMode = ListViewSelectionMode.Extended;
            ShowsScrollingPlaceholders = false;
            SingleSelectionFollowsFocus = true;

            DragItemsCompleted += ItemDragCompleted;
            DragItemsStarting += ItemDragStarting;
            SelectionChanged += SelectedItemsChanged;
            ProcessKeyboardAccelerators += ProcessDelKey;
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            internalScrollviewer = GetTemplateChild(PART_ScrollViewer) as ScrollViewer;

            if (internalScrollviewer == null)
            {
                logger.Warning("WinoListView does not have an internal ScrollViewer. Infinite scrolling behavior might be effected.");
                return;
            }

            internalScrollviewer.ViewChanged -= InternalScrollVeiwerViewChanged;
            internalScrollviewer.ViewChanged += InternalScrollVeiwerViewChanged;
        }

        private static void OnIsThreadViewChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoListView winoListView)
            {
                winoListView.AdjustThreadViewContainerVisuals();
            }
        }

        private void AdjustThreadViewContainerVisuals()
        {
            if (IsThreadListView)
            {
                ItemContainerTransitions.Clear();
            }
        }

        private double lastestRaisedOffset = 0;
        private int lastItemSize = 0;

        // TODO: This is buggy. Does not work all the time. Debug.

        private void InternalScrollVeiwerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (internalScrollviewer == null) return;

            // No need to raise init request if there are no items in the list.
            if (Items.Count == 0) return;

            // If the scrolling is finished, check the current viewport height.
            if (e.IsIntermediate)
            {
                var currentOffset = internalScrollviewer.VerticalOffset;
                var maxOffset = internalScrollviewer.ScrollableHeight;

                if (currentOffset + 10 >= maxOffset && lastestRaisedOffset != maxOffset && Items.Count != lastItemSize)
                {
                    // We must load more.
                    lastestRaisedOffset = maxOffset;
                    lastItemSize = Items.Count;

                    LoadMoreCommand?.Execute(null);
                }
            }
        }

        private void ProcessDelKey(UIElement sender, Windows.UI.Xaml.Input.ProcessKeyboardAcceleratorEventArgs args)
        {
            if (args.Key == Windows.System.VirtualKey.Delete)
            {
                args.Handled = true;

                ItemDeletedCommand?.Execute((int)MailOperation.SoftDelete);
            }
        }

        private void ItemDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            if (args.Items.Any(a => a is MailItemViewModel))
            {
                args.Items.Cast<MailItemViewModel>().ForEach(a => a.IsCustomFocused = false);
            }
        }

        private void ItemDragStarting(object sender, DragItemsStartingEventArgs args)
        {
            // Dragging multiple mails from different accounts/folders are supported with the condition below:
            // All mails belongs to the drag will be matched on the dropped folder's account.
            // Meaning that if users drag 1 mail from Account A/Inbox and 1 mail from Account B/Inbox,
            // and drop to Account A/Inbox, the mail from Account B/Inbox will NOT be moved.

            if (IsThreadListView)
            {
                var allItems = args.Items.Cast<MailItemViewModel>();

                // Highlight all items
                allItems.ForEach(a => a.IsCustomFocused = true);

                // Set native drag arg properties.

                var dragPackage = new MailDragPackage(allItems.Cast<IMailItem>());

                args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
            }
            else
            {
                var dragPackage = new MailDragPackage(args.Items.Cast<IMailItem>());

                args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
            }
        }

        public void ChangeSelectionMode(ListViewSelectionMode selectionMode)
        {
            SelectionMode = selectionMode;

            if (!IsThreadListView)
            {
                Items.Where(a => a is ThreadMailItemViewModel).Cast<ThreadMailItemViewModel>().ForEach(c =>
                {
                    var threadListView = GetThreadInternalListView(c);

                    if (threadListView != null)
                    {
                        threadListView.SelectionMode = selectionMode;
                    }
                });
            }
        }

        /// <summary>
        /// Finds the container for given mail item and adds it to selected items.
        /// </summary>
        /// <param name="mailItemViewModel">Mail to be added to selected items.</param>
        /// <returns>Whether selection was successful or not.</returns>
        public bool SelectMailItemContainer(MailItemViewModel mailItemViewModel)
        {
            var itemContainer = ContainerFromItem(mailItemViewModel);

            // This item might be in thread container.
            if (itemContainer == null)
            {
                bool found = false;

                Items.OfType<ThreadMailItemViewModel>().ForEach(c =>
                {
                    if (!found)
                    {
                        var threadListView = GetThreadInternalListView(c);

                        if (threadListView != null)
                            found = threadListView.SelectMailItemContainer(mailItemViewModel);
                    }
                });

                return found;
            }

            SelectedItems.Add(mailItemViewModel);
            return true;
        }

        /// <summary>
        /// Recursively clears all selections except the given mail.
        /// </summary>
        /// <param name="exceptViewModel">Exceptional mail item to be not unselected.</param>
        /// <param name="preserveThreadExpanding">Whether expansion states of thread containers should stay as it is or not.</param>
        public void ClearSelections(MailItemViewModel exceptViewModel = null, bool preserveThreadExpanding = false)
        {
            SelectedItems.Clear();

            Items.Where(a => a is ThreadMailItemViewModel).Cast<ThreadMailItemViewModel>().ForEach(c =>
            {
                var threadListView = GetThreadInternalListView(c);

                if (threadListView == null)
                    return;

                if (exceptViewModel != null)
                {
                    if (!threadListView.SelectedItems.Contains(exceptViewModel))
                    {
                        if (!preserveThreadExpanding)
                        {
                            c.IsThreadExpanded = false;
                        }

                        threadListView.SelectedItems.Clear();
                    }
                }
                else
                {
                    if (!preserveThreadExpanding)
                    {
                        c.IsThreadExpanded = false;
                    }

                    threadListView.SelectedItems.Clear();
                }
            });
        }

        /// <summary>
        /// Recursively selects all mails, including thread items.
        /// </summary>
        public void SelectAllWino()
        {
            SelectAll();

            Items.Where(a => a is ThreadMailItemViewModel).Cast<ThreadMailItemViewModel>().ForEach(c =>
            {
                c.IsThreadExpanded = true;

                var threadListView = GetThreadInternalListView(c);

                threadListView?.SelectAll();
            });
        }

        // SelectedItems changed.
        private void SelectedItemsChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.RemovedItems != null)
            {
                foreach (var removedItem in e.RemovedItems)
                {
                    if (removedItem is MailItemViewModel removedMailItemViewModel)
                    {
                        // Mail item un-selected.

                        removedMailItemViewModel.IsSelected = false;
                        WeakReferenceMessenger.Default.Send(new MailItemSelectionRemovedEvent(removedMailItemViewModel));
                    }
                    else if (removedItem is ThreadMailItemViewModel removedThreadItemViewModel)
                    {
                        removedThreadItemViewModel.IsThreadExpanded = false;
                    }
                }
            }

            if (e.AddedItems != null)
            {
                foreach (var addedItem in e.AddedItems)
                {
                    if (addedItem is MailItemViewModel addedMailItemViewModel)
                    {
                        // Mail item selected.

                        addedMailItemViewModel.IsSelected = true;

                        WeakReferenceMessenger.Default.Send(new MailItemSelectedEvent(addedMailItemViewModel));
                    }
                    else if (addedItem is ThreadMailItemViewModel threadMailItemViewModel)
                    {
                        if (IsThreadScrollingEnabled)
                        {
                            if (internalScrollviewer != null && ContainerFromItem(threadMailItemViewModel) is FrameworkElement threadFrameworkElement)
                            {
                                internalScrollviewer.ScrollToElement(threadFrameworkElement, true, true, bringToTopOrLeft: true);
                            }
                        }

                        // Try to select first item.
                        if (GetThreadInternalListView(threadMailItemViewModel) is WinoListView internalListView)
                        {
                            internalListView.SelectFirstItem();
                        }
                    }
                }
            }

            if (!IsThreadListView)
            {
                if (SelectionMode == ListViewSelectionMode.Extended && SelectedItems.Count == 1)
                {
                    // Only 1 single item is selected in extended mode for main list view.
                    // We should un-select all thread items.

                    Items.Where(a => a is ThreadMailItemViewModel).Cast<ThreadMailItemViewModel>().ForEach(c =>
                    {
                        // c.IsThreadExpanded = false;

                        var threadListView = GetThreadInternalListView(c);

                        threadListView?.SelectedItems.Clear();
                    });
                }
            }
            else
            {
                if (SelectionMode == ListViewSelectionMode.Extended && SelectedItems.Count == 1)
                {
                    // Tell main list view to unselect all his items.

                    //if (SelectedItems[0] is MailItemViewModel selectedMailItemViewModel)
                    //{
                    //    WeakReferenceMessenger.Default.Send(new ResetSingleMailItemSelectionEvent(selectedMailItemViewModel));
                    //}
                }
            }
        }

        public async void SelectFirstItem()
        {
            if (Items.Count > 0)
            {
                if (Items[0] is MailItemViewModel firstMailItemViewModel)
                {
                    // Make sure the invisible container is realized.
                    await Task.Delay(250);

                    if (ContainerFromItem(firstMailItemViewModel) is ListViewItem firstItemContainer)
                    {
                        firstItemContainer.IsSelected = true;
                    }

                    firstMailItemViewModel.IsSelected = true;

                    // WeakReferenceMessenger.Default.Send(new MailItemSelectedEvent(firstMailItemViewModel));
                }
            }
        }

        private WinoListView GetThreadInternalListView(ThreadMailItemViewModel threadMailItemViewModel)
        {
            var itemContainer = ContainerFromItem(threadMailItemViewModel);

            if (itemContainer is ListViewItem listItem)
            {
                var expander = listItem.GetChildByName<WinoExpander>("ThreadExpander");

                if (expander != null)
                    return expander.Content as WinoListView;
            }

            return null;
        }

        public void Dispose()
        {
            DragItemsCompleted -= ItemDragCompleted;
            DragItemsStarting -= ItemDragStarting;
            SelectionChanged -= SelectedItemsChanged;
            ProcessKeyboardAccelerators -= ProcessDelKey;

            if (internalScrollviewer != null)
            {
                internalScrollviewer.ViewChanged -= InternalScrollVeiwerViewChanged;
            }
        }

        protected override DependencyObject GetContainerForItemOverride()
        {
            var itemContainer = base.GetContainerForItemOverride();

            // Adjust scrolling margin for all containers.
            // I don't want to override the default style for this.

            if (itemContainer is ListViewItem listViewItem)
            {
                listViewItem.Margin = new Thickness(0, 0, 12, 4);
            }

            return itemContainer;
        }
    }
}
