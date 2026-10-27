using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Interfaces;
using Wino.Views.Abstract;
using Wino.Helpers;
using Wino.Mail.WinUI;

namespace Wino.Views.Settings;

public sealed partial class CreateEmailTemplatePage : CreateEmailTemplatePageAbstract
{
    public CreateEmailTemplatePage()
    {
        InitializeComponent();
        WebViewEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();
    }

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var htmlContent = await ViewModel.LoadAsync(e.Parameter);
        var preferences = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();
        await WebViewEditor.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
        await WebViewEditor.RenderHtmlAsync(htmlContent);

        if (!ViewModel.IsExistingTemplate)
        {
            TemplateNameTextBox.Focus(FocusState.Programmatic);
        }
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        WebViewEditor.Dispose();
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAsync(await WebViewEditor.GetHtmlBodyAsync() ?? string.Empty);
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteAsync();
    }

    private void EditorLinkOpenRequested(object? sender, Uri uri) => EditorLinkLauncher.Open(uri);
}
