using System.Drawing;
using System.Windows.Forms;

namespace Wino.Companion.Tray;

internal sealed class CompanionTrayIcon : IDisposable
{
    private readonly NotifyIcon notifyIcon;

    public CompanionTrayIcon(
        string iconPath,
        Func<Task> launchMail,
        Func<Task> launchCalendar,
        Func<Task> exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(CreateItem("Mail", launchMail));
        menu.Items.Add(CreateItem("Calendar", launchCalendar));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("Exit", exit));

        notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = new Icon(iconPath),
            Text = "Wino Mail & Calendar",
            Visible = true,
        };
        notifyIcon.DoubleClick += async (_, _) => await RunAsync(launchMail).ConfigureAwait(true);
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Icon?.Dispose();
        notifyIcon.Dispose();
    }

    private static ToolStripMenuItem CreateItem(string text, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += async (_, _) => await RunAsync(action).ConfigureAwait(true);
        return item;
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch
        {
            // A failed tray action must not terminate the resident companion.
        }
    }
}
