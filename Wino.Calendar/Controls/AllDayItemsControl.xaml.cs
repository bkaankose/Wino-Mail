using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Calendar;


namespace Wino.Calendar.Controls
{
    public sealed partial class AllDayItemsControl : UserControl
    {
        #region Dependency Properties

        public static readonly DependencyProperty CalendarDayModelProperty = DependencyProperty.Register(nameof(CalendarDayModel), typeof(CalendarDayModel), typeof(AllDayItemsControl), new PropertyMetadata(null));

        public CalendarDayModel CalendarDayModel
        {
            get { return (CalendarDayModel)GetValue(CalendarDayModelProperty); }
            set { SetValue(CalendarDayModelProperty, value); }
        }

        #endregion

        public AllDayItemsControl()
        {
            InitializeComponent();
        }
    }
}
