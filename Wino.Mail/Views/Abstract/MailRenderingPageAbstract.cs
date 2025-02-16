using Windows.UI.Xaml;
using Wino.Core.UWP;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract;

public abstract class MailRenderingPageAbstract : BasePage<MailRenderingPageViewModel>
{
    public bool IsDarkEditor
    {
        get { return (bool)GetValue(IsDarkEditorProperty); }
        set { SetValue(IsDarkEditorProperty, value); }
    }

    public static readonly DependencyProperty IsDarkEditorProperty = DependencyProperty.Register(nameof(IsDarkEditor), typeof(bool), typeof(MailRenderingPageAbstract), new PropertyMetadata(false, OnIsComposerDarkModeChanged));

    private static void OnIsComposerDarkModeChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is MailRenderingPageAbstract page)
        {
            page.OnEditorThemeChanged();
        }
    }

    public virtual void OnEditorThemeChanged() { }
}
