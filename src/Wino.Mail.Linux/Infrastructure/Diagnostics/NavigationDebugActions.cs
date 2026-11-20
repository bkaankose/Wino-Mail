#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary><c>debug-navigate</c> <c>s</c>: navigates to the named <see cref="WinoPage"/> and logs the result.</summary>
internal static class NavigationDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-navigate", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var name = args.Parameter?.GetString(out var _);
                if (!Enum.TryParse<WinoPage>(name, out var page)) throw new ArgumentException($"'{name}' is not a page.");
                if (services()?.GetService<INavigationService>() is not { } navigation) throw new InvalidOperationException("The app has not started.");
                var result = navigation.Navigate(page);
                Serilog.Log.Information("Debug action: navigate to {Page} returned {Result}.", page, result);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-navigate failed."); }
        };
        application.AddAction(action);
    }
}
#endif
