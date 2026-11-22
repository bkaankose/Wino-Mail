using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Native;

/// <summary>
/// <c>org.freedesktop.portal.Background.RequestBackground</c> for a sandboxed app: the portal asks the user and writes
/// the autostart entry itself. The answer arrives as the <c>Response</c> signal of the request object, so the signal is
/// subscribed on the predicted request path before the call. Runs on the GTK main thread, whose main context delivers it.
/// </summary>
internal static class BackgroundPortal
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static bool IsSandboxed
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("WINO_FORCE_PORTAL") == "1") return true;
#endif
            return File.Exists("/.flatpak-info");
        }
    }

    /// <returns>The <c>autostart</c> answer, or null when the request failed, was cancelled or timed out.</returns>
    public static async Task<bool?> RequestAsync(IDispatcher dispatcher, bool autostart, string[] commandLine, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Gio.DBusConnection? connection = null;
        uint subscription = 0;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            try
            {
                connection = Gio.Functions.BusGetSync(Gio.BusType.Session, null);
                var token = "wino" + Guid.NewGuid().ToString("N");
                var sender = (connection.GetUniqueName() ?? string.Empty).TrimStart(':').Replace('.', '_');
                var requestPath = $"{ObjectPath}/request/{sender}/{token}";
                subscription = connection.SignalSubscribe(Destination, "org.freedesktop.portal.Request", "Response", requestPath, null,
                    Gio.DBusSignalFlags.None,
                    (_, _, _, _, _, parameters) => answer.TrySetResult(ReadAnswer(parameters)));

                var options = GLib.Variant.NewArray(GLib.VariantType.New("{sv}"),
                [
                    Entry("handle_token", GLib.Variant.NewString(token)),
                    Entry("reason", GLib.Variant.NewString("Wino Mail checks for new mail and reminders after you sign in.")),
                    Entry("autostart", GLib.Variant.NewBoolean(autostart)),
                    Entry("commandline", GLib.Variant.NewStrv(commandLine)),
                    Entry("dbus-activatable", GLib.Variant.NewBoolean(false)),
                ]);
                connection.CallSync(Destination, ObjectPath, "org.freedesktop.portal.Background", "RequestBackground",
                    GLib.Variant.NewTuple([GLib.Variant.NewString(string.Empty), options]), GLib.VariantType.New("(o)"),
                    Gio.DBusCallFlags.None, 10000, null);
            }
            catch (Exception exception)
            {
                Serilog.Log.Warning(exception, "The Background portal request failed.");
                answer.TrySetResult(null);
            }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        await using (timeout.Token.Register(() => answer.TrySetResult(null)))
        {
            var result = await answer.Task;
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (subscription != 0) connection?.SignalUnsubscribe(subscription);
            });
            return result;
        }
    }

    private static GLib.Variant Entry(string key, GLib.Variant value) => GLib.Variant.NewDictEntry(GLib.Variant.NewString(key), GLib.Variant.NewVariant(value));

    private static bool? ReadAnswer(GLib.Variant parameters)
    {
        if (parameters.GetChildValue(0).GetUint32() != 0) return null;
        return parameters.GetChildValue(1).LookupValue("autostart", GLib.VariantType.New("b"))?.GetBoolean() ?? false;
    }
}
