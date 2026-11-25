#if DEBUG
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Platform.Linux.Notifications;
using Wino.Services.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-notify</c> <c>as</c> (PL-02): mail n|text (test notifications for the n newest Inbox mails, or the newest
/// whose subject contains text), new n (the new-mail path with the n newest, summary rules included), reminder [text],
/// attention, task, people, live, last [n] (the sent requests with their buttons and targets), respond id|last
/// default|index (activates the response action with that request's stored target, the path a notification server
/// takes), withdrawn. One log line <c>Debug action debug-notify &lt;verb&gt;: &lt;result&gt;</c>; a last argument
/// ending in .txt also receives it.
/// </summary>
internal static class NotifyDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-notify", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        var verb = arguments.Length > 0 ? arguments[0] : string.Empty;
        string? path = arguments.Length > 1 && arguments[^1].EndsWith(".txt", StringComparison.Ordinal) ? arguments[^1] : null;
        var rest = arguments.Length > 1 ? arguments[1..(path is null ? arguments.Length : arguments.Length - 1)] : [];
        string result;
        try
        {
            var provider = services() ?? throw new InvalidOperationException("no services");
            var builder = provider.GetRequiredService<INotificationBuilder>() as LinuxNotificationBuilder ?? throw new InvalidOperationException("not the Linux builder");
            result = verb switch
            {
                "mail" => await MailAsync(provider, builder, rest, test: true),
                "new" => await MailAsync(provider, builder, rest, test: false),
                "reminder" => await ReminderAsync(provider, builder, string.Join(' ', rest)),
                "attention" => await AttentionAsync(provider, builder),
                "task" => await TaskAsync(builder),
                "task-reminder" => await TaskReminderAsync(provider, builder, rest),
                "people" => await PeopleAsync(builder),
                "live" => await LiveAsync(builder),
                "last" => Last(builder, rest.Length > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 1),
                "respond" => Respond(provider, builder, rest),
                "withdrawn" => string.Join(", ", builder.Withdrawn),
                _ => "verbs: mail, new, reminder, attention, task, task-reminder, people, live, last, respond, withdrawn",
            };
        }
        catch (Exception exception)
        {
            result = "error: " + exception.Message;
            Serilog.Log.Error(exception, "Debug action debug-notify {Verb} failed.", verb);
        }
        Serilog.Log.Information("Debug action debug-notify {Verb}: {Result}", verb, result);
        if (path is not null) DebugActions.WriteText(path, result);
    }

    private static async Task<string> MailAsync(IServiceProvider provider, LinuxNotificationBuilder builder, string[] args, bool test)
    {
        var accounts = await provider.GetRequiredService<IAccountService>().GetAccountsAsync();
        var account = accounts.FirstOrDefault() ?? throw new InvalidOperationException("no account");
        var inbox = await provider.GetRequiredService<IFolderService>().GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox)
            ?? throw new InvalidOperationException("no Inbox");
        var mails = (await provider.GetRequiredService<IMailService>().GetMailsByFolderIdAsync(inbox.Id)).OrderByDescending(static mail => mail.CreationDate).ToList();
        var key = string.Join(' ', args);
        var picked = int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? mails.Take(Math.Max(1, count)).ToList()
            : mails.Where(mail => mail.Subject?.Contains(key, StringComparison.OrdinalIgnoreCase) == true).Take(1).ToList();
        if (picked.Count == 0) return "no mail " + key;
        var before = builder.Sent.Count;
        if (test) await builder.CreateTestNotificationsAsync(picked);
        else await builder.CreateNotificationsAsync(picked);
        await Task.Delay(200);
        return $"mails={picked.Count} sent={builder.Sent.Count - before} | " + Last(builder, builder.Sent.Count - before);
    }

    private static async Task<string> ReminderAsync(IServiceProvider provider, LinuxNotificationBuilder builder, string text)
    {
        var calendar = provider.GetRequiredService<ICalendarService>();
        var items = await calendar.SearchCalendarItemsAsync(string.IsNullOrWhiteSpace(text) ? "a" : text, 5);
        var item = items.FirstOrDefault() ?? throw new InvalidOperationException("no calendar item " + text);
        var full = await calendar.GetCalendarItemAsync(item.Id) ?? item;
        var before = builder.Sent.Count;
        await builder.CreateTestCalendarReminderNotificationAsync(full);
        await Task.Delay(200);
        return builder.Sent.Count > before ? Last(builder, 1) : "suppressed";
    }

    private static async Task<string> AttentionAsync(IServiceProvider provider, LinuxNotificationBuilder builder)
    {
        var account = (await provider.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault() ?? throw new InvalidOperationException("no account");
        var before = builder.Sent.Count;
        builder.CreateAttentionRequiredNotification(account);
        await Task.Delay(300);
        return builder.Sent.Count > before ? Last(builder, 1) : "suppressed";
    }

    private static async Task<string> TaskAsync(LinuxNotificationBuilder builder)
    {
        await builder.CreateTestTaskReminderNotificationAsync(new Wino.Core.Domain.Entities.Shared.AccountTask { Id = Guid.NewGuid(), Title = "PL02 task" });
        await Task.Delay(200);
        return Last(builder, 1);
    }

    private static async Task<string> TaskReminderAsync(IServiceProvider provider, LinuxNotificationBuilder builder, string[] rest)
    {
        var task = rest.Length > 0 && Guid.TryParse(rest[0], out var id)
            ? await provider.GetRequiredService<ITaskService>().GetTaskAsync(id) ?? throw new ArgumentException("no task " + rest[0])
            : new Wino.Core.Domain.Entities.Shared.AccountTask { Id = Guid.NewGuid(), Title = "MP15 task", DueDate = DateTime.Today };
        var before = builder.Sent.Count;
        await builder.CreateTaskReminderNotificationAsync(task);
        await Task.Delay(200);
        return builder.Sent.Count > before ? Last(builder, 1) : "suppressed";
    }

    private static async Task<string> PeopleAsync(LinuxNotificationBuilder builder)
    {
        await builder.CreateTestPeopleNotificationAsync(new Wino.Core.Domain.Entities.Shared.AccountContact { Id = Guid.NewGuid(), DisplayName = "PL02 person" });
        await Task.Delay(200);
        return Last(builder, 1);
    }

    private static async Task<string> LiveAsync(LinuxNotificationBuilder builder)
    {
        await builder.CreateLiveNotificationsTestNotificationAsync();
        await Task.Delay(200);
        return Last(builder, 1);
    }

    private static string Last(LinuxNotificationBuilder builder, int count)
    {
        var text = new StringBuilder();
        foreach (var request in builder.Sent.Skip(Math.Max(0, builder.Sent.Count - count)))
        {
            text.Append("id=").Append(request.Id).Append(" title='").Append(request.Title).Append("' body='").Append(request.Body.Replace("\n", "\\n", StringComparison.Ordinal))
                .Append("' icon=").Append(request.IconPath is null ? "app" : "file").Append(" priority=").Append(request.Priority).Append(" category=").Append(request.Category)
                .Append(" buttons=[").Append(string.Join(", ", request.Buttons.Select(static button => button.Label + "->" + button.ActionIdentifier)))
                .Append("] values=[").Append(string.Join(", ", request.Values.Select(static pair => pair.Key + "=" + pair.Value))).Append("] ; ");
        }
        return text.ToString();
    }

    private static string Respond(IServiceProvider provider, LinuxNotificationBuilder builder, string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("respond id|last default|index");
        var request = args[0] == "last" ? builder.Sent.LastOrDefault() : builder.Sent.LastOrDefault(item => item.Id == args[0]);
        if (request is null) return "no request " + args[0];
        var target = args[1] == "default"
            ? LinuxNotificationActions.Encode(NotificationResponseKind.Default, string.Empty, request.Category, request.Values)
            : LinuxNotificationActions.Encode(NotificationResponseKind.Action, request.Buttons[int.Parse(args[1], CultureInfo.InvariantCulture)].ActionIdentifier, request.Category, request.Values);
        Gio.Application.GetDefault()?.ActivateAction(LinuxNotificationActions.ActionName, target);
        return "activated " + request.Id + " " + args[1];
    }
}
#endif
