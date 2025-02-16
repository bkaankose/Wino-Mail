using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.UWP.Controls;
using Wino.Helpers;

namespace Wino.MenuFlyouts
{
    public partial class FilterMenuFlyout : MenuFlyout
    {
        public static readonly DependencyProperty SelectedFilterChangedCommandProperty = DependencyProperty.Register(nameof(SelectedFilterChangedCommand), typeof(IRelayCommand<FilterOption>), typeof(FilterMenuFlyout), new PropertyMetadata(null));
        public static readonly DependencyProperty FilterOptionsProperty = DependencyProperty.Register(nameof(FilterOptions), typeof(List<FilterOption>), typeof(FilterMenuFlyout), new PropertyMetadata(null, new PropertyChangedCallback(OnOptionsChanged)));
        public static readonly DependencyProperty SelectedFilterOptionProperty = DependencyProperty.Register(nameof(SelectedFilterOption), typeof(FilterOption), typeof(FilterMenuFlyout), new PropertyMetadata(null, OnSelectedFilterOptionChanged));
        public static readonly DependencyProperty SelectedSortingOptionProperty = DependencyProperty.Register(nameof(SelectedSortingOption), typeof(SortingOption), typeof(FilterMenuFlyout), new PropertyMetadata(null, new PropertyChangedCallback(OnSelectedSortingOptionChanged)));
        public static readonly DependencyProperty SortingOptionsProperty = DependencyProperty.Register(nameof(SortingOptions), typeof(List<SortingOption>), typeof(FilterMenuFlyout), new PropertyMetadata(null, new PropertyChangedCallback(OnOptionsChanged)));
        public static readonly DependencyProperty SelectedSortingOptionChangedCommandProperty = DependencyProperty.Register(nameof(SelectedSortingOptionChangedCommand), typeof(IRelayCommand<SortingOption>), typeof(FilterMenuFlyout), new PropertyMetadata(null));

        public IRelayCommand<FilterOption> SelectedFilterChangedCommand
        {
            get { return (IRelayCommand<FilterOption>)GetValue(SelectedFilterChangedCommandProperty); }
            set { SetValue(SelectedFilterChangedCommandProperty, value); }
        }

        public IRelayCommand<SortingOption> SelectedSortingOptionChangedCommand
        {
            get { return (IRelayCommand<SortingOption>)GetValue(SelectedSortingOptionChangedCommandProperty); }
            set { SetValue(SelectedSortingOptionChangedCommandProperty, value); }
        }

        public List<FilterOption> FilterOptions
        {
            get { return (List<FilterOption>)GetValue(FilterOptionsProperty); }
            set { SetValue(FilterOptionsProperty, value); }
        }

        public List<SortingOption> SortingOptions
        {
            get { return (List<SortingOption>)GetValue(SortingOptionsProperty); }
            set { SetValue(SortingOptionsProperty, value); }
        }

        public FilterOption SelectedFilterOption
        {
            get { return (FilterOption)GetValue(SelectedFilterOptionProperty); }
            set { SetValue(SelectedFilterOptionProperty, value); }
        }

        public SortingOption SelectedSortingOption
        {
            get { return (SortingOption)GetValue(SelectedSortingOptionProperty); }
            set { SetValue(SelectedSortingOptionProperty, value); }
        }

        private static void OnSelectedFilterOptionChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is FilterMenuFlyout bar)
            {
                bar.SelectFilterOption(bar.SelectedFilterOption);
                bar.SelectedFilterChangedCommand?.Execute(bar.SelectedFilterOption);
            }
        }

        private static void OnSelectedSortingOptionChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is FilterMenuFlyout bar)
            {
                bar.SelectSortingOption(bar.SelectedSortingOption);
                bar.SelectedSortingOptionChangedCommand?.Execute(bar.SelectedSortingOption);
            }
        }

        private ToggleMenuFlyoutItem CreateFilterToggleButton(FilterOption option)
        {
            var button = new ToggleMenuFlyoutItem()
            {
                Text = option.Title,
                Tag = option,
                Icon = new WinoFontIcon() { Icon = XamlHelpers.GetWinoIconGlyph(option.Type) },
                IsChecked = option == SelectedFilterOption
            };

            button.Click += FilterToggleChecked;

            return button;
        }

        private ToggleMenuFlyoutItem CreateSortingToggleButton(SortingOption option)
        {
            var button = new ToggleMenuFlyoutItem()
            {
                Text = option.Title,
                Tag = option,
                Icon = new WinoFontIcon() { Icon = XamlHelpers.GetWinoIconGlyph(option.Type)},
                IsChecked = option == SelectedSortingOption
            };

            button.Click += SortingOptionChecked;

            return button;
        }

        private void SortingOptionChecked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem button)
            {
                button.IsHitTestVisible = false;

                var optionModel = button.Tag as SortingOption;

                SelectSortingOption(optionModel);
            }
        }



        private void FilterToggleChecked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem button)
            {
                button.IsHitTestVisible = false;

                var optionModel = button.Tag as FilterOption;

                SelectFilterOption(optionModel);
            }
        }

        private void SelectFilterOption(FilterOption option)
        {
            SelectedFilterOption = option;

            UncheckOtherFilterOptions();
        }

        private void SelectSortingOption(SortingOption option)
        {
            SelectedSortingOption = option;

            UncheckOtherSortingOptions();
        }

        private void UnregisterCheckedHandler(ToggleMenuFlyoutItem button)
        {
            button.Click -= FilterToggleChecked;
        }

        private void UncheckOtherFilterOptions()
        {
            if (Items.Any())
            {
                foreach (var item in Items)
                {
                    if (item is ToggleMenuFlyoutItem toggleButton && toggleButton.Tag is FilterOption option && option != SelectedFilterOption)
                    {
                        toggleButton.IsChecked = false;
                        toggleButton.IsHitTestVisible = true;
                    }
                }
            }
        }

        private void UncheckOtherSortingOptions()
        {
            if (Items.Any())
            {
                foreach (var item in Items)
                {
                    if (item is ToggleMenuFlyoutItem toggleButton && toggleButton.Tag is SortingOption option && option != SelectedSortingOption)
                    {
                        toggleButton.IsChecked = false;
                        toggleButton.IsHitTestVisible = true;
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var item in Items)
            {
                if (item is ToggleMenuFlyoutItem toggleButton)
                {
                    UnregisterCheckedHandler(toggleButton);
                }
            }
        }

        private static void OnOptionsChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is FilterMenuFlyout bar && bar.SortingOptions != null && bar.FilterOptions != null)
            {
                bar.Dispose();

                bar.Items.Clear();

                if (bar.FilterOptions != null)
                {
                    foreach (var item in bar.FilterOptions)
                    {
                        bar.Items.Add(bar.CreateFilterToggleButton(item));
                    }
                }

                bar.Items.Add(new MenuFlyoutSeparator());

                // Sorting options.

                if (bar.SortingOptions != null)
                {
                    foreach (var item in bar.SortingOptions)
                    {
                        bar.Items.Add(bar.CreateSortingToggleButton(item));
                    }
                }
            }
        }
    }
}
