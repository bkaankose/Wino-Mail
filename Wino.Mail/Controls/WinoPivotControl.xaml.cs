using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Wino.Extensions;

namespace Wino.Controls
{
    // TODO: Memory leak with FolderPivot bindings.
    public sealed partial class WinoPivotControl : UserControl
    {
        private Compositor _compositor;
        private ShapeVisual _shapeVisual;
        private CompositionSpriteShape _spriteShape;
        private CompositionRoundedRectangleGeometry _roundedRectangle;

        public event EventHandler<SelectionChangedEventArgs> SelectionChanged;

        public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(WinoPivotControl), new PropertyMetadata(null));
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(WinoPivotControl), new PropertyMetadata(null));
        public static readonly DependencyProperty SelectorPipeColorProperty = DependencyProperty.Register(nameof(SelectorPipeColor), typeof(SolidColorBrush), typeof(WinoPivotControl), new PropertyMetadata(Colors.Transparent, OnSelectorPipeColorChanged));
        public static readonly DependencyProperty DataTemplateProperty = DependencyProperty.Register(nameof(DataTemplate), typeof(DataTemplate), typeof(WinoPivotControl), new PropertyMetadata(null));

        public DataTemplate DataTemplate
        {
            get { return (DataTemplate)GetValue(DataTemplateProperty); }
            set { SetValue(DataTemplateProperty, value); }
        }

        public SolidColorBrush SelectorPipeColor
        {
            get { return (SolidColorBrush)GetValue(SelectorPipeColorProperty); }
            set { SetValue(SelectorPipeColorProperty, value); }
        }

        public object SelectedItem
        {
            get { return (object)GetValue(SelectedItemProperty); }
            set { SetValue(SelectedItemProperty, value); }
        }

        public object ItemsSource
        {
            get { return (object)GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        private static void OnSelectorPipeColorChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoPivotControl control)
            {
                control.UpdateSelectorPipeColor();
            }
        }

        private void UpdateSelectorPipeColor()
        {
            if (_spriteShape != null && _compositor != null)
            {
                _spriteShape.FillBrush = _compositor.CreateColorBrush(SelectorPipeColor.Color);
            }
        }

        private void CreateSelectorVisuals()
        {
            _compositor = this.Visual().Compositor;

            _roundedRectangle = _compositor.CreateRoundedRectangleGeometry();
            _roundedRectangle.CornerRadius = new Vector2(3, 3);

            _spriteShape = _compositor.CreateSpriteShape(_roundedRectangle);
            _spriteShape.CenterPoint = new Vector2(100, 100);

            _shapeVisual = _compositor.CreateShapeVisual();

            _shapeVisual.Shapes.Clear();
            _shapeVisual.Shapes.Add(_spriteShape);

            SelectorPipe.SetChildVisual(_shapeVisual);

            _shapeVisual.EnableImplicitAnimation(VisualPropertyType.Size, 400);
        }

        public WinoPivotControl()
        {
            this.InitializeComponent();

            CreateSelectorVisuals();
        }

        private bool IsContainerPresent()
        {
            return SelectedItem != null && PivotHeaders.ContainerFromItem(SelectedItem) != null;
        }

        private void PivotHeaders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateVisuals();

            SelectionChanged?.Invoke(sender, e);
        }

        private void UpdateVisuals()
        {
            MoveSelector();
        }

        private void UpdateSelectorVisibility()
        {
            SelectorPipe.Visibility = IsContainerPresent() ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void MoveSelector()
        {
            if (PivotHeaders.SelectedItem != null)
            {
                // Get selected item container position
                // TODO: It's bad...
                while(PivotHeaders.ContainerFromItem(PivotHeaders.SelectedItem) == null)
                {
                    await Task.Delay(100);
                }

                UpdateSelectorVisibility();

                var container = PivotHeaders.ContainerFromItem(PivotHeaders.SelectedItem) as FrameworkElement;

                if (container != null)
                {
                    var transformToVisual = container.TransformToVisual(this);
                    Point screenCoords = transformToVisual.TransformPoint(new Point(0, 0));

                    float actualWidth = 0, leftMargin = 0, translateX = 0;

                    leftMargin = (float)(screenCoords.X);

                    if (PivotHeaders.Items.Count > 1)
                    {
                        // Multiple items, pipe is centered.

                        actualWidth = (float)(container.ActualWidth + 12) / 2;
                        translateX = leftMargin - 10 + (actualWidth / 2);
                    }
                    else
                    {
                        actualWidth = (float)(container.ActualWidth) - 12;
                        translateX = leftMargin + 4;
                    }

                    SelectorPipe.Width = actualWidth;
                    SelectorPipe.Translation = new Vector3(translateX, 0, 0);
                }
                else
                {
                    Debug.WriteLine("Container null");
                }
            }
        }

        private void SelectorPipeSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _roundedRectangle.Size = e.NewSize.ToVector2();
            _shapeVisual.Size = e.NewSize.ToVector2();
        }

        private void ControlUnloaded(object sender, RoutedEventArgs e)
        {
            //PivotHeaders.SelectionChanged -= PivotHeaders_SelectionChanged;
            //PivotHeaders.SelectedItem = null;

            //SelectedItem = null;
            //ItemsSource = null;
        }

        private void ControlLoaded(object sender, RoutedEventArgs e)
        {
            // Bindings.Update();
        }
    }
}
