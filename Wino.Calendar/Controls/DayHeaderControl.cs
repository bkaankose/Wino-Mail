using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Calendar.Controls
{
    public class DayHeaderControl : Control
    {
        private const string PART_DayHeaderTextBlock = nameof(PART_DayHeaderTextBlock);
        private TextBlock HeaderTextblock;

        public DayHeaderDisplayType DisplayType
        {
            get { return (DayHeaderDisplayType)GetValue(DisplayTypeProperty); }
            set { SetValue(DisplayTypeProperty, value); }
        }

        public DateTime Date
        {
            get { return (DateTime)GetValue(DateProperty); }
            set { SetValue(DateProperty, value); }
        }

        public static readonly DependencyProperty DateProperty = DependencyProperty.Register(nameof(Date), typeof(DateTime), typeof(DayHeaderControl), new PropertyMetadata(default(DateTime), new PropertyChangedCallback(OnHeaderPropertyChanged)));
        public static readonly DependencyProperty DisplayTypeProperty = DependencyProperty.Register(nameof(DisplayType), typeof(DayHeaderDisplayType), typeof(DayHeaderControl), new PropertyMetadata(DayHeaderDisplayType.TwentyFourHour, new PropertyChangedCallback(OnHeaderPropertyChanged)));

        public DayHeaderControl()
        {
            DefaultStyleKey = typeof(DayHeaderControl);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            HeaderTextblock = GetTemplateChild(PART_DayHeaderTextBlock) as TextBlock;
            UpdateHeaderText();
        }

        private static void OnHeaderPropertyChanged(DependencyObject control, DependencyPropertyChangedEventArgs e)
        {
            if (control is DayHeaderControl headerControl)
            {
                headerControl.UpdateHeaderText();
            }
        }

        private void UpdateHeaderText()
        {
            if (HeaderTextblock != null)
            {
                HeaderTextblock.Text = DisplayType == DayHeaderDisplayType.TwelveHour ? Date.ToString("h tt") : Date.ToString("HH:mm");
            }
        }
    }
}
