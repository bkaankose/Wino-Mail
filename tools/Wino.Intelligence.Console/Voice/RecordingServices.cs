using System.Collections;
using System.Reflection;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Enums;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Messaging.Server;

namespace Wino.Intelligence.ConsoleApp.Voice;

/// <summary>One thing the app would have done: queued a request or opened a window.</summary>
internal sealed record RecordedOperation(string Kind, string Description);

/// <summary>
/// Collects the operations of the running case. The runner runs cases in parallel, so each case's execution sets
/// <see cref="Current"/> on its own async flow.
/// </summary>
internal static class OperationLog
{
    private static readonly AsyncLocal<List<RecordedOperation>?> _current = new();

    public static List<RecordedOperation>? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public static void Add(string kind, string description)
    {
        var list = Current;
        if (list is null)
            return;

        lock (list)
            list.Add(new RecordedOperation(kind, description));
    }
}

/// <summary>Stands in for the request delegator: records what would be queued and sends nothing to any server.</summary>
internal sealed class RecordingRequestDelegator : IWinoRequestDelegator
{
    public Task ExecuteAsync(MailOperationPreperationRequest request)
    {
        RecordMail(request);
        return Task.CompletedTask;
    }

    public Task<WinoRequestDelegationResult> ExecuteWithResultAsync(MailOperationPreperationRequest request)
    {
        var count = RecordMail(request);
        return Task.FromResult(new WinoRequestDelegationResult(count, null));
    }

    public Task ExecuteAsync(DraftPreparationRequest request)
    {
        var text = request.Account?.Address ?? string.Empty;
        if (request.ReferenceMailCopy is { } reference)
            text += $" reply-to=\"{Describer.Clip(reference.Subject, 50)}\" from {reference.FromAddress}";
        text += $" reason={request.Reason}";
        try
        {
            if (request.CreatedLocalDraftMimeMessage is { } mime)
            {
                var to = string.Join(", ", mime.To.Mailboxes.Select(static mailbox => mailbox.Address));
                var cc = string.Join(", ", mime.Cc.Mailboxes.Select(static mailbox => mailbox.Address));
                text += $" to=[{to}]" + (cc.Length > 0 ? $" cc=[{cc}]" : string.Empty);
                if (!string.IsNullOrWhiteSpace(mime.Subject))
                    text += $" subject=\"{mime.Subject}\"";
                var body = mime.TextBody ?? System.Text.RegularExpressions.Regex.Replace(mime.HtmlBody ?? string.Empty, "<[^>]+>", " ");
                if (!string.IsNullOrWhiteSpace(body))
                    text += $" body=\"{Describer.Clip(System.Net.WebUtility.HtmlDecode(body).Trim(), 120)}\"";
            }
        }
        catch (Exception exception)
        {
            text += $" (mime unreadable: {exception.GetType().Name})";
        }

        OperationLog.Add("Draft", text);
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(SendDraftPreparationRequest request)
    {
        OperationLog.Add("SendDraft", Describer.Describe(request));
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(FolderOperationPreperationRequest request)
    {
        OperationLog.Add("Folder." + request.Action, Describer.Describe(request));
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(CalendarOperationPreparationRequest request)
    {
        var compose = request.ComposeResult;
        var text = request.CalendarItem is { } item
            ? Describer.Event(item)
            : compose is null ? string.Empty
            : $"\"{compose.Title}\" {(compose.IsAllDay ? $"all-day {compose.StartDate:ddd yyyy-MM-dd}" : $"{compose.StartDate:ddd yyyy-MM-dd HH:mm}-{compose.EndDate:HH:mm}")}"
              + (string.IsNullOrWhiteSpace(compose.Location) ? string.Empty : $" at \"{compose.Location}\"") + $" showAs={compose.ShowAs}";
        if (request.Attendees is { Count: > 0 } attendees)
            text += $" attendees=[{string.Join(", ", attendees.Select(static a => a.Email))}]";
        if (request.ComposeResult?.Attendees is { Count: > 0 } composeAttendees)
            text += $" attendees=[{string.Join(", ", composeAttendees.Select(static a => a.Email))}]";
        if (!string.IsNullOrWhiteSpace(request.ResponseMessage))
            text += $" message=\"{request.ResponseMessage}\"";
        OperationLog.Add("Calendar." + request.Operation, text.Trim());
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(ContactOperationPreparationRequest request)
    {
        OperationLog.Add("Contact." + request.Operation, Describer.Describe(request));
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(IReadOnlyList<ContactOperationPreparationRequest> requests)
    {
        foreach (var request in requests)
            OperationLog.Add("Contact." + request.Operation, Describer.Describe(request));
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(Guid accountId, IEnumerable<IRequestBase> requests)
    {
        foreach (var request in requests)
        {
            var kind = request.GetType().Name.Replace("Request", string.Empty);
            if (request.GetType().GetProperty("Operation")?.GetValue(request) is { } operation && operation.ToString() != kind)
                kind += "." + operation;
            var task = request.GetType().GetProperty("Task")?.GetValue(request);
            var text = task is null ? Describer.Describe(request) : Describer.Describe(task);
            if (request.GetType().GetProperty("Step")?.GetValue(request) is { } step)
                text += $" step=[{Describer.Describe(step)}]";
            OperationLog.Add(kind, text);
        }
        return Task.CompletedTask;
    }

    public Task ExecuteLocalAsync(IRequestBase request)
    {
        OperationLog.Add("Local." + request.GetType().Name, Describer.Describe(request));
        return Task.CompletedTask;
    }

    private static int RecordMail(MailOperationPreperationRequest request)
    {
        var mails = request.MailItems?.ToList() ?? [];
        var sample = string.Join("; ", mails.Take(3).Select(static mail => $"\"{Describer.Clip(mail.Subject, 50)}\" from {mail.FromAddress}"));
        var target = request.MoveTargetFolder is { } folder ? $" -> {folder.FolderName}" : string.Empty;
        OperationLog.Add("Mail." + request.Action, $"{mails.Count} mail(s){target}{(sample.Length > 0 ? ": " + sample : string.Empty)}{(mails.Count > 3 ? " ..." : string.Empty)}");
        return mails.Count;
    }
}

/// <summary>Stands in for the clipboard: records the text instead of copying it.</summary>
internal sealed class RecordingClipboardService : IClipboardService
{
    public Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        OperationLog.Add("Clipboard", $"\"{Describer.Clip(text, 60)}\"");
        return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
    }
}

/// <summary>Stands in for the launcher: records the file or link instead of opening it.</summary>
internal sealed class RecordingExternalLauncher : IExternalLauncher
{
    public Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default)
    {
        OperationLog.Add("LaunchFile", path);
        return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
    }

    public Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        OperationLog.Add("LaunchUri", uri.ToString());
        return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
    }
}

/// <summary>Stands in for the one-click unsubscribe: records the link and posts nothing.</summary>
internal sealed class RecordingUnsubscriptionService : IUnsubscriptionService
{
    public Task<bool> OneClickUnsubscribeAsync(UnsubscribeInfo info)
    {
        OperationLog.Add("Unsubscribe", Describer.Clip(info.HttpLink, 120));
        return Task.FromResult(true);
    }
}

/// <summary>Records the synchronizations a plan asks for; the runner never synchronizes.</summary>
internal sealed class RecordingMessageListener
{
    public static RecordingMessageListener Instance { get; } = new();

    public void Register()
    {
        WeakReferenceMessenger.Default.Register<RecordingMessageListener, NewMailSynchronizationRequested>(this, static (_, message) =>
        {
            var folders = message.Options.SynchronizationFolderIds is { Count: > 0 } ids ? $" folders={ids.Count}" : string.Empty;
            OperationLog.Add("Sync." + message.Options.Type, $"account={message.Options.AccountId}{folders}");
        });
    }
}

/// <summary>Stands in for the app's windows: records what the plan would open.</summary>
internal sealed class RecordingPlanHost : IVoicePlanHost
{
    public Task OpenSearchAsync(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSearchCriteria criteria)
    {
        var folderText = string.Join(", ", folders.Take(6).Select(static folder => folder.FolderName)) + (folders.Count > 6 ? $" (+{folders.Count - 6})" : string.Empty);
        OperationLog.Add("OpenSearch", $"{account.Address} folders=[{folderText}] {Describer.Describe(criteria)}");
        return Task.CompletedTask;
    }

    public Task OpenMailAsync(MailCopy mail)
    {
        OperationLog.Add("OpenMail", $"\"{Describer.Clip(mail.Subject, 60)}\" from {mail.FromAddress} {mail.CreationDate:g}");
        return Task.CompletedTask;
    }

    public Task OpenDraftAsync(MailAccount account, MailCopy draft)
    {
        OperationLog.Add("OpenDraft", $"{account.Address} \"{Describer.Clip(draft.Subject, 60)}\"");
        return Task.CompletedTask;
    }

    public Task OpenEventAsync(CalendarItem calendarItem)
    {
        OperationLog.Add("OpenEvent", Describer.Event(calendarItem));
        return Task.CompletedTask;
    }

    public Task OpenEventEditorAsync(CalendarEventComposeNavigationArgs args)
    {
        var attendees = args.Attendees.Count > 0 ? $" attendees=[{string.Join(", ", args.Attendees.Select(Describer.Describe))}]" : string.Empty;
        OperationLog.Add("OpenEventEditor",
            $"\"{args.Title}\" {(args.IsAllDay ? $"all-day {args.StartDate:ddd yyyy-MM-dd}" : $"{args.StartDate:ddd yyyy-MM-dd HH:mm}-{args.EndDate:HH:mm}")}" +
            (string.IsNullOrWhiteSpace(args.Location) ? string.Empty : $" at \"{args.Location}\"") +
            (args.ShowAs is { } showAs ? $" showAs={showAs}" : string.Empty) +
            (args.Recurrence is not null ? " recurring" : string.Empty) + attendees);
        return Task.CompletedTask;
    }
}

internal static class Describer
{
    public static string Clip(string? text, int length)
    {
        text = (text ?? string.Empty).ReplaceLineEndings(" ");
        return text.Length <= length ? text : text[..length] + "...";
    }

    public static string Event(CalendarItem item)
        => $"\"{item.Title}\" {(item.IsAllDayEvent ? $"all-day {item.StartDate:ddd yyyy-MM-dd}" : $"{item.StartDate:ddd yyyy-MM-dd HH:mm}-{item.EndDate:HH:mm}")}" +
           (string.IsNullOrWhiteSpace(item.Location) ? string.Empty : $" at \"{item.Location}\"");

    /// <summary>The scalar properties of an object that have a value, one level deep.</summary>
    public static string Describe(object? value)
    {
        if (value is null)
            return "null";
        if (IsScalar(value.GetType()))
            return Format(value);

        var parts = new List<string>();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.Name is "EqualityContract")
                continue;

            object? item;
            try
            {
                item = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (item is null || (item is string text && text.Length == 0) || item is false)
                continue;

            if (IsScalar(property.PropertyType) || IsScalar(Nullable.GetUnderlyingType(property.PropertyType) ?? typeof(object)))
            {
                if (item is Guid guid && guid == Guid.Empty)
                    continue;
                parts.Add($"{property.Name}={Format(item)}");
            }
            else if (item is IEnumerable sequence and not string)
            {
                var items = sequence.Cast<object?>().Take(8).Select(static element => element is null ? "null" : IsScalar(element.GetType()) ? Format(element) : ShortName(element)).ToList();
                if (items.Count > 0)
                    parts.Add($"{property.Name}=[{string.Join(", ", items)}]");
            }
            else
            {
                parts.Add($"{property.Name}={ShortName(item)}");
            }
        }

        return string.Join(" ", parts);
    }

    private static string ShortName(object value) => value switch
    {
        MailAccount account => account.Address,
        IMailItemFolder folder => folder.FolderName,
        MailCopy mail => $"\"{Clip(mail.Subject, 40)}\"",
        CalendarItem item => Event(item),
        _ => TryNameOrAddress(value) ?? value.GetType().Name,
    };

    private static string? TryNameOrAddress(object value)
    {
        foreach (var name in new[] { "Address", "Email", "EmailAddress", "Name", "DisplayName", "Title" })
        {
            if (value.GetType().GetProperty(name)?.GetValue(value) is string text && text.Length > 0)
                return text;
        }

        return null;
    }

    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly)
        || type == typeof(TimeSpan) || type == typeof(decimal);

    private static string Format(object value) => value switch
    {
        string text => $"\"{Clip(text, 80)}\"",
        DateTime time => time.ToString("yyyy-MM-dd HH:mm"),
        DateTimeOffset time => time.ToString("yyyy-MM-dd HH:mm zzz"),
        _ => value.ToString() ?? string.Empty,
    };
}
