using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Wino.AppServices.Contracts;
using Wino.AppServices.Contracts.Generated;
using Wino.Companion.Notifications;
using Wino.Companion.Services;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Services;
using Wino.Services;

namespace Wino.Companion.Backend;

/// <summary>
/// Owns every database and synchronization service. The UWP project never references
/// this graph and therefore cannot open SQLite or instantiate an authenticator.
/// </summary>
internal sealed class CompanionBackendHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int initialized;

    public CompanionBackendHost(Func<nint> interactiveWindowProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreServices();
        services.RegisterSharedServices();

        services.AddSingleton<IConfigurationService, CompanionConfigurationService>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IStoreManagementService, CompanionStoreManagementService>();
        services.AddSingleton(new HeadlessNativeAppService
        {
            GetCoreWindowHwnd = () => interactiveWindowProvider(),
        });
        services.AddSingleton<INativeAppService>(static serviceProvider => serviceProvider.GetRequiredService<HeadlessNativeAppService>());
        services.AddSingleton<IAppMetadataService>(static serviceProvider => serviceProvider.GetRequiredService<HeadlessNativeAppService>());
        services.AddSingleton<HeadlessMailDialogService>();
        services.AddSingleton<IMailDialogService>(static serviceProvider => serviceProvider.GetRequiredService<HeadlessMailDialogService>());
        services.AddSingleton<IDialogServiceBase>(static serviceProvider => serviceProvider.GetRequiredService<HeadlessMailDialogService>());
        services.AddSingleton<IKeyPressService, HeadlessKeyPressService>();
        services.AddSingleton<INotificationBuilder, CompanionNotificationBuilder>();
        services.AddSingleton<CompanionBackendControl>();
        services.AddSingleton<ICompanionBackendControl>(static serviceProvider => serviceProvider.GetRequiredService<CompanionBackendControl>());

        provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false,
        });

        var paths = provider.GetRequiredService<IApplicationConfiguration>();
        paths.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
        paths.PublisherSharedFolderPath = ApplicationData.Current
            .GetPublisherCacheFolder(Wino.Services.ApplicationConfiguration.SharedFolderName)
            .Path;
        paths.ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path;
    }

    public IWinoRpcRequestHandler Dispatcher =>
        WinoRpcDispatcher.FromServices(provider);

    public ICompanionBackendControl Control => provider.GetRequiredService<ICompanionBackendControl>();

    public ISynchronizationManager SynchronizationManager => provider.GetRequiredService<ISynchronizationManager>();

    public INotificationBuilder Notifications => provider.GetRequiredService<INotificationBuilder>();

    public async Task CreateMailNotificationsAsync(MailNotificationsRequest request, CancellationToken cancellationToken)
    {
        var mailService = provider.GetRequiredService<IMailService>();
        var mails = new List<Wino.Core.Domain.Entities.Mail.MailCopy>(request.MailIds.Count);
        foreach (var mailId in request.MailIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mail = await mailService.GetSingleMailItemAsync(mailId).ConfigureAwait(false);
            if (mail is not null)
            {
                mails.Add(mail);
            }
        }

        await Notifications.CreateNotificationsAsync(mails).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAccountAttentionNotificationAsync(
        AccountAttentionNotificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await provider.GetRequiredService<IAccountService>()
            .GetAccountAsync(request.AccountId)
            .ConfigureAwait(false);
        if (account is not null)
        {
            Notifications.CreateAttentionRequiredNotification(account);
        }
    }

    public async Task CreateCalendarReminderNotificationAsync(
        CalendarReminderNotificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var calendarItem = await provider.GetRequiredService<ICalendarService>()
            .GetCalendarItemAsync(request.CalendarItemId)
            .ConfigureAwait(false);
        if (calendarItem is not null)
        {
            await Notifications.CreateCalendarReminderNotificationAsync(
                    calendarItem,
                    request.ReminderDurationInSeconds)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task HandleToastActionAsync(BackgroundToastActionRequest request, CancellationToken cancellationToken)
    {
        var arguments = ParseArguments(request.Arguments);
        if (arguments.ContainsKey(Wino.Core.Domain.Constants.ToastDismissActionKey))
        {
            return;
        }

        if (arguments.TryGetValue(Wino.Core.Domain.Constants.ToastModeKey, out var calendarMode) &&
            string.Equals(calendarMode, Wino.Core.Domain.Constants.ToastModeCalendar, StringComparison.OrdinalIgnoreCase))
        {
            await HandleCalendarToastActionAsync(arguments, request.UserInput, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!arguments.TryGetValue(Wino.Core.Domain.Constants.ToastModeKey, out var mode) ||
            !string.Equals(mode, Wino.Core.Domain.Constants.ToastModeMail, StringComparison.OrdinalIgnoreCase) ||
            !arguments.TryGetValue(Wino.Core.Domain.Constants.ToastMailUniqueIdKey, out var mailIdText) ||
            !Guid.TryParse(mailIdText, out var mailId) ||
            !arguments.TryGetValue(Wino.Core.Domain.Constants.ToastActionKey, out var actionText) ||
            !Enum.TryParse<MailOperation>(actionText, true, out var action))
        {
            throw new InvalidOperationException("The background toast action is invalid or requires foreground UI.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mail = await provider.GetRequiredService<IMailService>().GetSingleMailItemAsync(mailId).ConfigureAwait(false);
        if (mail is null)
        {
            return;
        }

        await provider.GetRequiredService<IWinoRequestDelegator>()
            .ExecuteAsync(new MailOperationPreperationRequest(action, mail))
            .ConfigureAwait(false);
        await provider.GetRequiredService<INotificationBuilder>().UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
    }

    private async Task HandleCalendarToastActionAsync(
        IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> userInput,
        CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue(Wino.Core.Domain.Constants.ToastCalendarActionKey, out var action) ||
            !arguments.TryGetValue(Wino.Core.Domain.Constants.ToastCalendarItemIdKey, out var itemIdText) ||
            !Guid.TryParse(itemIdText, out var itemId))
        {
            throw new InvalidOperationException("The background calendar toast action is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var calendarService = provider.GetRequiredService<ICalendarService>();
        if (string.Equals(action, Wino.Core.Domain.Constants.ToastCalendarSnoozeAction, StringComparison.Ordinal))
        {
            var snoozeMinutes = provider.GetRequiredService<IPreferencesService>().DefaultSnoozeDurationInMinutes;
            if (userInput.TryGetValue(Wino.Core.Domain.Constants.ToastCalendarSnoozeDurationInputId, out var selected) &&
                int.TryParse(selected, out var parsedMinutes) && parsedMinutes > 0)
            {
                snoozeMinutes = parsedMinutes;
            }

            if (snoozeMinutes > 0)
            {
                await calendarService.SnoozeCalendarItemAsync(itemId, DateTime.Now.AddMinutes(snoozeMinutes)).ConfigureAwait(false);
            }

            return;
        }

        if (string.Equals(action, Wino.Core.Domain.Constants.ToastCalendarJoinOnlineAction, StringComparison.Ordinal))
        {
            var calendarItem = await calendarService.GetCalendarItemAsync(itemId).ConfigureAwait(false);
            if (calendarItem is not null && Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out var joinUri))
            {
                await provider.GetRequiredService<INativeAppService>().LaunchUriAsync(joinUri).ConfigureAwait(false);
            }

            return;
        }

        throw new InvalidOperationException("The calendar toast action requires foreground UI.");
    }

    /// <summary>
    /// Completes when the database, translations and the synchronization manager are
    /// usable. The AppService connection is opened before initialization finishes, so
    /// every backend-dependent RPC awaits this before dispatching.
    /// </summary>
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        ready.Task.WaitAsync(cancellationToken);

    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref initialized, 1) != 0)
        {
            await ready.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await provider.GetRequiredService<IDatabaseService>().InitializeAsync().ConfigureAwait(false);
            await provider.GetRequiredService<ITranslationService>().InitializeAsync().ConfigureAwait(false);
            await provider.GetRequiredService<SynchronizationManagerInitializer>().InitializeAsync().ConfigureAwait(false);
            ready.TrySetResult();
        }
        catch (Exception exception)
        {
            // Pending gated RPCs must fail instead of waiting forever for a backend
            // that will never come up. The companion process exits right after.
            ready.TrySetException(exception);
            throw;
        }
    }

    public ValueTask DisposeAsync() => provider.DisposeAsync();

    private static Dictionary<string, string> ParseArguments(string arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in (arguments ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? segment : segment[..separator].Replace('+', ' '));
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(segment[(separator + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }
}
