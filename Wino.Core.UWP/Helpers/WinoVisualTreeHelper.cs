﻿using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Wino.Helpers
{
    public static class WinoVisualTreeHelper
    {
        public static T GetChildObject<T>(DependencyObject obj, string name) where T : FrameworkElement
        {
            DependencyObject child = null;
            T grandChild = null;

            for (int i = 0; i <= VisualTreeHelper.GetChildrenCount(obj) - 1; i++)
            {
                child = VisualTreeHelper.GetChild(obj, i);

                if (child is T && (((T)child).Name == name | string.IsNullOrEmpty(name)))
                {
                    return (T)child;
                }
                else
                {
                    grandChild = GetChildObject<T>(child, name);
                }
                if (grandChild != null)
                {
                    return grandChild;
                }
            }
            return null;
        }
    }
}
