using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Settings;

public sealed partial class CreateEmailTemplatePage : CreateEmailTemplatePageAbstract
{
    public CreateEmailTemplatePage()
    {
        InitializeComponent();
        TemplateEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();
    }

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var htmlContent = await ViewModel.LoadAsync(e.Parameter);
        var preferences = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();

        try
        {
            await TemplateEditor.Session.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
            await TemplateEditor.Session.RenderHtmlAsync(htmlContent ?? string.Empty);
        }
        catch (ObjectDisposedException)
        {
            // The page was left before the editor showed the template.
            return;
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The template editor failed to load the template.");
        }

        if (!ViewModel.IsExistingTemplate)
        {
            TemplateNameTextBox.Focus(FocusState.Programmatic);
        }
    }

    protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        await TemplateEditor.Session.DisposeAsync();
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        string htmlContent;

        try
        {
            htmlContent = await TemplateEditor.Session.GetHtmlBodyAsync() ?? string.Empty;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The editor never showed the template, so its content would replace the saved one with nothing.
            return;
        }

        await ViewModel.SaveAsync(htmlContent);
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteAsync();
    }

    private void EditorLinkOpenRequested(object? sender, Uri uri) => EditorLinkLauncher.Open(uri);
}
