using System;
using System.ComponentModel;
using System.Numerics;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Extensions;
using Wino.Mail.ViewModels.Data;

namespace Wino.Controls
{
    public sealed partial class MailItemDisplayInformationControl : UserControl, INotifyPropertyChanged
    {
        public ImagePreviewControl GetImagePreviewControl() => ContactImage;

        public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(nameof(DisplayMode), typeof(MailListDisplayMode), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailListDisplayMode.Spacious));
        public static readonly DependencyProperty ShowPreviewTextProperty = DependencyProperty.Register(nameof(ShowPreviewText), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true));
        public static readonly DependencyProperty SnippetProperty = DependencyProperty.Register(nameof(Snippet), typeof(string), typeof(MailItemDisplayInformationControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty FromNameProperty = DependencyProperty.Register(nameof(FromName), typeof(string), typeof(MailItemDisplayInformationControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty SubjectProperty = DependencyProperty.Register(nameof(Subject), typeof(string), typeof(MailItemDisplayInformationControl), new PropertyMetadata("(no-subject)"));
        public static readonly DependencyProperty IsReadProperty = DependencyProperty.Register(nameof(IsRead), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
        public static readonly DependencyProperty IsFlaggedProperty = DependencyProperty.Register(nameof(IsFlagged), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
        public static readonly DependencyProperty FromAddressProperty = DependencyProperty.Register(nameof(FromAddress), typeof(string), typeof(MailItemDisplayInformationControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty HasAttachmentsProperty = DependencyProperty.Register(nameof(HasAttachments), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
        public static readonly DependencyProperty IsCustomFocusedProperty = DependencyProperty.Register(nameof(IsCustomFocused), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
        public static readonly DependencyProperty ReceivedDateProperty = DependencyProperty.Register(nameof(ReceivedDate), typeof(DateTime), typeof(MailItemDisplayInformationControl), new PropertyMetadata(default(DateTime)));
        public static readonly DependencyProperty IsDraftProperty = DependencyProperty.Register(nameof(IsDraft), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));
        public static readonly DependencyProperty IsAvatarVisibleProperty = DependencyProperty.Register(nameof(IsAvatarVisible), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true));
        public static readonly DependencyProperty IsSubjectVisibleProperty = DependencyProperty.Register(nameof(IsSubjectVisible), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true));
        public static readonly DependencyProperty ConnectedExpanderProperty = DependencyProperty.Register(nameof(ConnectedExpander), typeof(Expander), typeof(MailItemDisplayInformationControl), new PropertyMetadata(null));
        public static readonly DependencyProperty LeftHoverActionProperty = DependencyProperty.Register(nameof(LeftHoverAction), typeof(MailOperation), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailOperation.None));
        public static readonly DependencyProperty CenterHoverActionProperty = DependencyProperty.Register(nameof(CenterHoverAction), typeof(MailOperation), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailOperation.None));
        public static readonly DependencyProperty RightHoverActionProperty = DependencyProperty.Register(nameof(RightHoverAction), typeof(MailOperation), typeof(MailItemDisplayInformationControl), new PropertyMetadata(MailOperation.None));
        public static readonly DependencyProperty HoverActionExecutedCommandProperty = DependencyProperty.Register(nameof(HoverActionExecutedCommand), typeof(ICommand), typeof(MailItemDisplayInformationControl), new PropertyMetadata(null));
        public static readonly DependencyProperty MailItemProperty = DependencyProperty.Register(nameof(MailItem), typeof(IMailItem), typeof(MailItemDisplayInformationControl), new PropertyMetadata(null));
        public static readonly DependencyProperty IsHoverActionsEnabledProperty = DependencyProperty.Register(nameof(IsHoverActionsEnabled), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(true));
        public static readonly DependencyProperty Prefer24HourTimeFormatProperty = DependencyProperty.Register(nameof(Prefer24HourTimeFormat), typeof(bool), typeof(MailItemDisplayInformationControl), new PropertyMetadata(false));

        public bool Prefer24HourTimeFormat
        {
            get { return (bool)GetValue(Prefer24HourTimeFormatProperty); }
            set { SetValue(Prefer24HourTimeFormatProperty, value); }
        }

        public bool IsHoverActionsEnabled
        {
            get { return (bool)GetValue(IsHoverActionsEnabledProperty); }
            set { SetValue(IsHoverActionsEnabledProperty, value); }
        }

        public IMailItem MailItem
        {
            get { return (IMailItem)GetValue(MailItemProperty); }
            set { SetValue(MailItemProperty, value); }
        }

        public ICommand HoverActionExecutedCommand
        {
            get { return (ICommand)GetValue(HoverActionExecutedCommandProperty); }
            set { SetValue(HoverActionExecutedCommandProperty, value); }
        }

        public MailOperation LeftHoverAction
        {
            get { return (MailOperation)GetValue(LeftHoverActionProperty); }
            set { SetValue(LeftHoverActionProperty, value); }
        }

        public MailOperation CenterHoverAction
        {
            get { return (MailOperation)GetValue(CenterHoverActionProperty); }
            set { SetValue(CenterHoverActionProperty, value); }
        }

        public MailOperation RightHoverAction
        {
            get { return (MailOperation)GetValue(RightHoverActionProperty); }
            set { SetValue(RightHoverActionProperty, value); }
        }


        public event PropertyChangedEventHandler PropertyChanged;

        public Expander ConnectedExpander
        {
            get { return (Expander)GetValue(ConnectedExpanderProperty); }
            set { SetValue(ConnectedExpanderProperty, value); }
        }

        public bool IsSubjectVisible
        {
            get { return (bool)GetValue(IsSubjectVisibleProperty); }
            set { SetValue(IsSubjectVisibleProperty, value); }
        }

        public bool IsAvatarVisible
        {
            get { return (bool)GetValue(IsAvatarVisibleProperty); }
            set { SetValue(IsAvatarVisibleProperty, value); }
        }

        public bool IsDraft
        {
            get { return (bool)GetValue(IsDraftProperty); }
            set { SetValue(IsDraftProperty, value); }
        }

        public DateTime ReceivedDate
        {
            get { return (DateTime)GetValue(ReceivedDateProperty); }
            set { SetValue(ReceivedDateProperty, value); }
        }
        public bool IsCustomFocused
        {
            get { return (bool)GetValue(IsCustomFocusedProperty); }
            set { SetValue(IsCustomFocusedProperty, value); }
        }

        public bool HasAttachments
        {
            get { return (bool)GetValue(HasAttachmentsProperty); }
            set { SetValue(HasAttachmentsProperty, value); }
        }

        public bool IsRead
        {
            get { return (bool)GetValue(IsReadProperty); }
            set { SetValue(IsReadProperty, value); }
        }

        public bool IsFlagged
        {
            get { return (bool)GetValue(IsFlaggedProperty); }
            set { SetValue(IsFlaggedProperty, value); }
        }

        public string FromAddress
        {
            get { return (string)GetValue(FromAddressProperty); }
            set
            {
                SetValue(FromAddressProperty, value);

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }

        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(FromName))
                    return FromAddress;

                return FromName;
            }
        }
        public string FromName
        {
            get => (string)GetValue(FromNameProperty);
            set
            {
                SetValue(FromNameProperty, value);

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }

        public string Subject
        {
            get { return (string)GetValue(SubjectProperty); }
            set { SetValue(SubjectProperty, value); }
        }

        public string Snippet
        {
            get { return (string)GetValue(SnippetProperty); }
            set { SetValue(SnippetProperty, value); }
        }

        public bool ShowPreviewText
        {
            get { return (bool)GetValue(ShowPreviewTextProperty); }
            set { SetValue(ShowPreviewTextProperty, value); }
        }

        public MailListDisplayMode DisplayMode
        {
            get { return (MailListDisplayMode)GetValue(DisplayModeProperty); }
            set { SetValue(DisplayModeProperty, value); }
        }

        private bool tappedHandlingFlag = false;

        public MailItemDisplayInformationControl()
        {
            this.InitializeComponent();

            var compositor = this.Visual().Compositor;

            var leftBackgroundVisual = compositor.CreateSpriteVisual();
            RootContainerVisualWrapper.SetChildVisual(leftBackgroundVisual);
            MainContentContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

            RootContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
            ContentGrid.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
            ContentStackpanel.EnableImplicitAnimation(VisualPropertyType.Offset, 400);
            IconsContainer.EnableImplicitAnimation(VisualPropertyType.Offset, 400);

            RootContainerVisualWrapper.SizeChanged += (s, e) => leftBackgroundVisual.Size = e.NewSize.ToVector2();
        }

        private void ControlPointerEntered(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (IsHoverActionsEnabled)
            {
                HoverActionButtons.Visibility = Visibility.Visible;
            }
        }

        private void ControlPointerExited(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (IsHoverActionsEnabled)
            {
                HoverActionButtons.Visibility = Visibility.Collapsed;
            }
        }

        private void ExecuteHoverAction(MailOperation operation)
        {
            MailOperationPreperationRequest package = null;

            if (MailItem is MailItemViewModel mailItemViewModel)
                package = new MailOperationPreperationRequest(operation, mailItemViewModel.MailCopy, toggleExecution: true);
            else if (MailItem is ThreadMailItemViewModel threadMailItemViewModel)
                package = new MailOperationPreperationRequest(operation, threadMailItemViewModel.GetMailCopies(), toggleExecution: true);

            if (package == null) return;

            tappedHandlingFlag = true;

            HoverActionExecutedCommand?.Execute(package);
        }

        private void ThreadHeaderTapped(object sender, TappedRoutedEventArgs e)
        {
            // Due to CanDrag=True, outer expander doesn't get the click event and it doesn't expand. We expand here manually.
            // Also hover action button clicks will be delegated here after the execution runs.
            // We should not expand the thread if the reason we are here is for hover actions.

            if (tappedHandlingFlag)
            {
                tappedHandlingFlag = false;
                e.Handled = true;
                return;
            }

            if (ConnectedExpander == null) return;

            ConnectedExpander.IsExpanded = !ConnectedExpander.IsExpanded;
        }

        private void FirstActionClicked(object sender, RoutedEventArgs e)
        {
            ExecuteHoverAction(LeftHoverAction);
        }

        private void SecondActionClicked(object sender, RoutedEventArgs e)
        {
            ExecuteHoverAction(CenterHoverAction);
        }

        private void ThirdActionClicked(object sender, RoutedEventArgs e)
        {
            ExecuteHoverAction(RightHoverAction);
        }
    }
}
