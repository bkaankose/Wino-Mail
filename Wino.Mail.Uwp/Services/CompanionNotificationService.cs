using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Wino.AppServices.Contracts;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Uwp.Services;

/// <summary>
/// UI-side notification facade. The packaged companion remains the only process
/// that creates toasts, updates either AUMID badge, or owns the JumpList.
/// </summary>
public sealed class CompanionNotificationService(CompanionConnectionService connection) : INotificationBuilder
{
    public Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems) =>
        InvokeAsync(
            "notification.create-mail.v1",
            new MailNotificationsRequest(newMailItems.Select(static mail => mail.UniqueId).ToArray()),
            WinoAppServiceJsonContext.Default.MailNotificationsRequest);

    public Task UpdateTaskbarIconBadgeAsync() =>
        InvokeAsync("notification.update-mail-badge.v1");

    public Task UpdateJumpListOptionsAsync() =>
        InvokeAsync("notification.update-jumplist.v1");

    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) =>
        InvokeAsync(
            "notification.add-calendar-badge.v1",
            new BadgeCountRequest(newlyDownloadedCount),
            WinoAppServiceJsonContext.Default.BadgeCountRequest);

    public Task ClearCalendarTaskbarBadgeAsync() =>
        InvokeAsync("notification.clear-calendar-badge.v1");

    public void RemoveNotification(Guid mailUniqueId) =>
        _ = InvokeAsync(
            "notification.remove-mail.v1",
            new RemoveMailNotificationRequest(mailUniqueId),
            WinoAppServiceJsonContext.Default.RemoveMailNotificationRequest);

    public void CreateAttentionRequiredNotification(MailAccount account) =>
        _ = InvokeAsync(
            "notification.account-attention.v1",
            new AccountAttentionNotificationRequest(account.Id),
            WinoAppServiceJsonContext.Default.AccountAttentionNotificationRequest);

    public void CreateWebView2RuntimeMissingNotification()
    {
        // This notification described a missing desktop WebView2 runtime. WinUI 2's
        // packaged WebView2 control is part of the UWP deployment, so the setting and
        // notification have no UWP equivalent.
    }

    public Task CreateCalendarReminderNotificationAsync(Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem, long reminderDurationInSeconds) =>
        InvokeAsync(
            "notification.calendar-reminder.v1",
            new CalendarReminderNotificationRequest(calendarItem.Id, reminderDurationInSeconds),
            WinoAppServiceJsonContext.Default.CalendarReminderNotificationRequest);

    private async Task InvokeAsync(string method)
    {
        var response = await connection.InvokeAsync(
            method,
            null,
            CancellationToken.None);

        if (!response.IsSuccess)
        {
            throw new WinoRemoteException(response);
        }
    }

    private async Task InvokeAsync<T>(string method, T payload, JsonTypeInfo<T> typeInfo)
    {
        var response = await connection.InvokeAsync(
            method,
            JsonSerializer.Serialize(payload, typeInfo),
            CancellationToken.None).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            throw new WinoRemoteException(response);
        }
    }
}
