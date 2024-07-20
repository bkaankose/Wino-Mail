#if NET8_0
using Microsoft.UI.Xaml;
#else
using Microsoft.UI.Xaml;
#endif

namespace Wino.Styles
{
    public partial class CommandBarItems : ResourceDictionary
    {
        public CommandBarItems()
        {
            InitializeComponent();
        }
    }
}
