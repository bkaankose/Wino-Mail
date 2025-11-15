using System;
using Microsoft.UI.Xaml.Navigation;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class AccountManagementPage : AccountManagementPageAbstract
{
    public AccountManagementPage()
    {
        InitializeComponent();

        NavigationCacheMode = NavigationCacheMode.Enabled;
    }
}
