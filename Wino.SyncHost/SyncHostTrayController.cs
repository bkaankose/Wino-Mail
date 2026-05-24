using Serilog;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Services;
using WpfApplication = System.Windows.Application;

namespace Wino.SyncHost;

internal sealed class SyncHostTrayController : IDisposable
{
    private readonly IPreferencesService _preferencesService;
    private readonly IAccountService _accountService;
    private readonly PackagedAppEntryLauncher _entryLauncher;
    private readonly SyncHostApplication _application;
    private readonly ILogger _logger = Log.ForContext<SyncHostTrayController>();
    private NotifyIcon? _trayIcon;

    public SyncHostTrayController(
        IPreferencesService preferencesService,
        IAccountService accountService,
        PackagedAppEntryLauncher entryLauncher,
        SyncHostApplication application)
    {
        _preferencesService = preferencesService;
        _accountService = accountService;
        _entryLauncher = entryLauncher;
        _application = application;
    }

    public void Create()
    {
        if (_trayIcon != null)
            return;

        if (_preferencesService.AppCloseBehavior != AppCloseBehavior.RunInBackgroundWithTrayIcon)
            return;

        var hasAccounts = _accountService.GetAccountsAsync().GetAwaiter().GetResult().Any();
        if (!hasAccounts)
            return;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Wino_Icon.ico");

        if (!File.Exists(iconPath))
        {
            _logger.Warning("Tray icon file was not found at {IconPath}.", iconPath);
            return;
        }

        _trayIcon = new NotifyIcon
        {
            Icon = new Icon(iconPath),
            Text = "Wino Mail",
            ContextMenuStrip = BuildTrayMenu(),
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => InvokeTrayAction(OpenDefaultAsync);
    }

    public void Dispose()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
        }

        _trayIcon = null;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(CreateMenuItem(Translator.SystemTrayMenu_Open, OpenDefaultAsync));
        menu.Items[0].Font = new Font(menu.Items[0].Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(CreateMenuItem(Translator.SystemTrayMenu_ShowWino, OpenMailAsync));
        menu.Items.Add(CreateMenuItem(Translator.SystemTrayMenu_ShowWinoCalendar, OpenCalendarAsync));
        menu.Items.Add(CreateMenuItem(Translator.SystemTrayMenu_ExitWino, ExitAsync));

        return menu;
    }

    private ToolStripMenuItem CreateMenuItem(string text, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => InvokeTrayAction(action);
        return item;
    }

    private void InvokeTrayAction(Func<Task> action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.CheckAccess())
        {
            _ = ExecuteTrayActionAsync(action);
            return;
        }

        dispatcher.InvokeAsync(() => _ = ExecuteTrayActionAsync(action));
    }

    private async Task ExecuteTrayActionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Sync host tray action failed.");
        }
    }

    private Task OpenDefaultAsync()
        => _entryLauncher.LaunchAsync(_preferencesService.DefaultApplicationMode);

    private Task OpenMailAsync()
        => _entryLauncher.LaunchAsync(WinoApplicationMode.Mail);

    private Task OpenCalendarAsync()
        => _entryLauncher.LaunchAsync(WinoApplicationMode.Calendar);

    private Task ExitAsync()
    {
        _application.RequestShutdown();
        return Task.CompletedTask;
    }
}
