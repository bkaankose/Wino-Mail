using System;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using MailCal.Core.Domain.Interfaces;
using MailCal.Mail.WinUI;

namespace MailCal.Helpers;

/// <summary>
/// Opens a link the user chose in a composer, through the app's launcher.
/// </summary>
internal static class EditorLinkLauncher
{
    public static async void Open(Uri uri)
    {
        try
        {
            var launcher = MailCalApplication.Current.Services.GetRequiredService<IExternalLauncher>();
            (await launcher.LaunchUriAsync(uri)).ThrowIfNotSucceeded();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open a link from the editor.");
        }
    }
}
