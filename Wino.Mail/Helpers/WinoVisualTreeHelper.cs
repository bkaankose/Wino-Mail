


#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
#else
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
#endif

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
