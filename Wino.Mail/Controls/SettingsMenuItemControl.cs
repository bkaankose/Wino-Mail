using System;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Wino.Controls
{
    /// <summary>
    /// Templated button for each setting in Settings Dialog.
    /// </summary>
    public class SettingsMenuItemControl : Control
    {
        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public string Description
        {
            get { return (string)GetValue(DescriptionProperty); }
            set { SetValue(DescriptionProperty, value); }
        }

        public FrameworkElement Icon
        {
            get { return (FrameworkElement)GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }


        public ICommand Command
        {
            get { return (ICommand)GetValue(CommandProperty); }
            set { SetValue(CommandProperty, value); }
        }



        public object CommandParameter
        {
            get { return (object)GetValue(CommandParameterProperty); }
            set { SetValue(CommandParameterProperty, value); }
        }

        public bool IsClickable
        {
            get { return (bool)GetValue(IsClickableProperty); }
            set { SetValue(IsClickableProperty, value); }
        }

        public bool IsNavigateIconVisible
        {
            get { return (bool)GetValue(IsNavigateIconVisibleProperty); }
            set { SetValue(IsNavigateIconVisibleProperty, value); }
        }

        public FrameworkElement SideContent
        {
            get { return (FrameworkElement)GetValue(SideContentProperty); }
            set { SetValue(SideContentProperty, value); }
        }

        public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(SettingsMenuItemControl), new PropertyMetadata(null));
        public static readonly DependencyProperty SideContentProperty = DependencyProperty.Register(nameof(SideContent), typeof(FrameworkElement), typeof(SettingsMenuItemControl), new PropertyMetadata(null));
        public static readonly DependencyProperty IsClickableProperty = DependencyProperty.Register(nameof(IsClickable), typeof(bool), typeof(SettingsMenuItemControl), new PropertyMetadata(true));
        public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(SettingsMenuItemControl), new PropertyMetadata(null));
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(FrameworkElement), typeof(SettingsMenuItemControl), new PropertyMetadata(null));
        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsMenuItemControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsMenuItemControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty IsNavigateIconVisibleProperty = DependencyProperty.Register(nameof(IsNavigateIconVisible), typeof(bool), typeof(SettingsMenuItemControl), new PropertyMetadata(true));
    }
}
