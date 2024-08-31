using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Microsoft.Xaml.Interactivity;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Wino.Controls;
using Wino.Core.Domain.Models.Menus;
using Wino.Helpers;

namespace Wino.Behaviors
{
    public class BindableCommandBarBehavior : Behavior<CommandBar>
    {
        public static readonly DependencyProperty PrimaryCommandsProperty = DependencyProperty.Register(
            "PrimaryCommands", typeof(object), typeof(BindableCommandBarBehavior),
            new PropertyMetadata(null, UpdateCommands));

        public static readonly DependencyProperty ItemTemplateSelectorProperty = DependencyProperty.Register(
            "ItemTemplateSelector", typeof(DataTemplateSelector), typeof(BindableCommandBarBehavior),
            new PropertyMetadata(null, null));

        public ICommand ItemClickedCommand
        {
            get { return (ICommand)GetValue(ItemClickedCommandProperty); }
            set { SetValue(ItemClickedCommandProperty, value); }
        }

        public static readonly DependencyProperty ItemClickedCommandProperty = DependencyProperty.Register(nameof(ItemClickedCommand), typeof(ICommand), typeof(BindableCommandBarBehavior), new PropertyMetadata(null));

        public DataTemplateSelector ItemTemplateSelector
        {
            get { return (DataTemplateSelector)GetValue(ItemTemplateSelectorProperty); }
            set { SetValue(ItemTemplateSelectorProperty, value); }
        }

        public object PrimaryCommands
        {
            get { return GetValue(PrimaryCommandsProperty); }
            set { SetValue(PrimaryCommandsProperty, value); }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();

            AttachChanges(false);

            if (PrimaryCommands is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is ButtonBase button)
                    {
                        button.Click -= Button_Click;
                    }
                }
            }
        }

        private void UpdatePrimaryCommands()
        {
            if (AssociatedObject == null)
                return;

            if (PrimaryCommands == null)
                return;

            if (AssociatedObject.PrimaryCommands is IEnumerable enumerableObjects)
            {
                foreach (var item in enumerableObjects)
                {
                    if (item is ButtonBase button)
                    {
                        button.Click -= Button_Click;
                    }
                }
            }

            if (AssociatedObject.SecondaryCommands is IEnumerable secondaryObject)
            {
                foreach (var item in secondaryObject)
                {
                    if (item is ButtonBase button)
                    {
                        button.Click -= Button_Click;
                    }
                }
            }

            AssociatedObject.PrimaryCommands.Clear();
            AssociatedObject.SecondaryCommands.Clear();

            if (!(PrimaryCommands is IEnumerable enumerable)) return;

            foreach (var command in enumerable)
            {
                if (command is MailOperationMenuItem mailOperationMenuItem)
                {
                    ICommandBarElement menuItem = null;

                    if (mailOperationMenuItem.Operation == Core.Domain.Enums.MailOperation.Seperator)
                    {
                        menuItem = new AppBarSeparator();
                    }
                    else
                    {
                        var label = XamlHelpers.GetOperationString(mailOperationMenuItem.Operation);
                        menuItem = new AppBarButton
                        {
                            Icon = new WinoFontIcon() { Glyph = ControlConstants.WinoIconFontDictionary[XamlHelpers.GetWinoIconGlyph(mailOperationMenuItem.Operation)] },
                            Label = label,
                            LabelPosition = string.IsNullOrWhiteSpace(label) ? CommandBarLabelPosition.Collapsed : CommandBarLabelPosition.Default,
                            DataContext = mailOperationMenuItem,
                        };

                        ToolTip toolTip = new ToolTip
                        {
                            Content = label
                        };
                        ToolTipService.SetToolTip((DependencyObject)menuItem, toolTip);

                        ((AppBarButton)menuItem).Click -= Button_Click;
                        ((AppBarButton)menuItem).Click += Button_Click;
                    }

                    if (mailOperationMenuItem.IsSecondaryMenuPreferred)
                    {
                        AssociatedObject.SecondaryCommands.Add(menuItem);
                    }
                    else
                    {
                        AssociatedObject.PrimaryCommands.Add(menuItem);
                    }
                }

                //if (dependencyObject is ICommandBarElement icommandBarElement)
                //{
                //    if (dependencyObject is ButtonBase button)
                //    {
                //        button.Click -= Button_Click;
                //        button.Click += Button_Click;
                //    }

                //    if (command is MailOperationMenuItem mailOperationMenuItem)
                //    {

                //    }
                //}
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            ItemClickedCommand?.Execute(((ButtonBase)sender).DataContext);
        }

        protected override void OnAttached()
        {
            base.OnAttached();

            AttachChanges(true);

            UpdatePrimaryCommands();
        }

        private void PrimaryCommandsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdatePrimaryCommands();
        }

        private static void UpdateCommands(DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
        {
            if (!(dependencyObject is BindableCommandBarBehavior behavior)) return;

            if (dependencyPropertyChangedEventArgs.OldValue is INotifyCollectionChanged oldList)
            {
                oldList.CollectionChanged -= behavior.PrimaryCommandsCollectionChanged;
            }

            behavior.AttachChanges(true);
            behavior.UpdatePrimaryCommands();
        }

        private void AttachChanges(bool register)
        {
            if (PrimaryCommands is null) return;

            if (PrimaryCommands is INotifyCollectionChanged collectionChanged)
            {
                if (register)
                {
                    collectionChanged.CollectionChanged -= PrimaryCommandsCollectionChanged;
                    collectionChanged.CollectionChanged += PrimaryCommandsCollectionChanged;
                }
                else
                    collectionChanged.CollectionChanged -= PrimaryCommandsCollectionChanged;
            }
        }
    }
}
