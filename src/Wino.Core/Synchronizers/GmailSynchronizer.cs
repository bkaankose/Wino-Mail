using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using CommunityToolkit.Mvvm.Messaging;
using global::Google.Apis.Calendar.v3.Data;
using global::Google.Apis.Gmail.v1;
using global::Google.Apis.Gmail.v1.Data;
using Google;
using MailKit;
using Microsoft.IdentityModel.Tokens;
using MimeKit;
using MoreLinq;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Google;
using Wino.Core.Helpers;
using Wino.Core.Http;
using Wino.Core.Integration.Processors;
using Wino.Core.Misc;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Tasks;
using Wino.Mail.AI.Abstractions;
using Wino.Messaging.UI;
using Wino.Services;
using DriveFile = global::Google.Apis.Drive.v3.Data.File;
using GmailFilter = global::Google.Apis.Gmail.v1.Data.Filter;
using GmailMessagePart = global::Google.Apis.Gmail.v1.Data.MessagePart;
using GoogleCalendarService = Wino.Core.Google.CalendarService;

namespace Wino.Core.Synchronizers.Mail;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Label))]
[JsonSerializable(typeof(Draft))]
[JsonSerializable(typeof(Event))]
public partial class GmailSynchronizerJsonContext : JsonSerializerContext;

/// <summary>
/// Gmail synchronizer implementation using Gmail History API for efficient incremental sync.
///
/// SYNCHRONIZATION STRATEGY:
/// - Initial sync: Downloads up to 15c00 messages PER FOLDER with metadata only.
///   Uses a global HashSet to track downloaded message IDs, avoiding duplicate downloads
///   when messages have multiple labels. Each folder gets its full quota of messages.
/// - Incremental sync: Uses ONLY History API to get changes since last sync.
///   No per-folder downloads during incremental sync - this is the proper Gmail sync approach.
/// - Messages are downloaded with metadata only during initial sync (no MIME content)
/// - New messages during incremental sync are downloaded with full MIME content
/// - MIME files for initial sync messages are downloaded on-demand when user reads a message
///
/// Key implementation details:
/// - PerformInitialreync: Downloads messages per-folder with global deduplication
/// - SynchronizeDeltaAsync: Processes incremental changes using History API with pagination
/// - Handles 404/410 errors (history expired) by triggering full resync
/// - CreateMinimalMailCopyAsync: Extracts MailCopy fields from Gmail Metadata format
/// - DownloadMissingMimeMessageAsync: Downloads raw MIME only when explicitly requested
/// </summary>
public class GmailSynchronizer : WinoSynchronizer<IGoogleApiRequest, Message, Event, global::Google.Apis.PeopleService.v1.Data.Person>, IProviderMailFilterSynchronizer, ISemanticMailBodySynchronizer
{
    public override uint BatchModificationSize => 1000;

    /// <summary>
    /// Legacy page size hint kept for compatibility with shared synchronizer contracts.
    /// Gmail initial sync now downloads all messages inside the selected cutoff window.
    /// </summary>
    public override uint InitialMessageDownloadCountPerFolder => 1500;

    // It's actually 100. But Gmail SDK has internal bug for Out of Memory exception.
    // https://github.com/googleapis/google-api-dotnet-client/issues/2603
    private const uint MaximumAllowedBatchRequestSize = 10;

    private readonly HttpClient _googleHttpClient;
    private readonly HttpClient _googleProviderFeatureHttpClient;
    private readonly GmailService _gmailService;
    private readonly GmailService _gmailFilterService;
    private readonly GoogleCalendarService _calendarService;
    private readonly DriveService _driveService;
    private readonly PeopleServiceService _peopleService;
    private readonly IContactService _contactService;
    private readonly IContactPictureFileService _contactPictureFileService;
    private readonly ITaskService _taskService;
    private readonly GoogleTasksClient _googleTasksClient;
    private readonly LocalTaskSynchronizer _localTaskSynchronizer = new();
    private readonly LocalContactSynchronizer _localContactSynchronizer = new();

    private readonly IGmailChangeProcessor _gmailChangeProcessor;
    private readonly IGmailSynchronizerErrorHandlerFactory _gmailSynchronizerErrorHandlerFactory;
    private readonly IMailFilterExecutor _mailFilterExecutor;
    private readonly ILogger _logger = Log.ForContext<GmailSynchronizer>();

    // Keeping a reference for quick access to the virtual archive folder.
    private Guid? archiveFolderId;
    private bool _isFolderStructureChanged;

    public GmailSynchronizer(MailAccount account,
                             IGmailAuthenticator authenticator,
                             IGmailChangeProcessor gmailChangeProcessor,
                             IGmailSynchronizerErrorHandlerFactory gmailSynchronizerErrorHandlerFactory,
                             IMailFilterExecutor mailFilterExecutor = null,
                             IContactService contactService = null,
                             IContactPictureFileService contactPictureFileService = null,
                             ITaskService taskService = null)
        : this(
            account,
            gmailChangeProcessor,
            gmailSynchronizerErrorHandlerFactory,
            new GmailClientMessageHandler(authenticator, account),
            mailFilterExecutor,
            new GmailClientMessageHandler(authenticator, account, [ProviderFeature.MailFilters]),
            contactService,
            contactPictureFileService,
            taskService)
    {
    }

    internal GmailSynchronizer(
        MailAccount account,
        IGmailChangeProcessor gmailChangeProcessor,
        IGmailSynchronizerErrorHandlerFactory gmailSynchronizerErrorHandlerFactory,
        HttpMessageHandler googleMessageHandler,
        IMailFilterExecutor mailFilterExecutor = null,
        HttpMessageHandler providerFeatureMessageHandler = null,
        IContactService contactService = null,
        IContactPictureFileService contactPictureFileService = null,
        ITaskService taskService = null) : base(account, WeakReferenceMessenger.Default)
    {
        _googleHttpClient = new HttpClient(googleMessageHandler, disposeHandler: true);
        _gmailService = new GmailService(_googleHttpClient);
        if (providerFeatureMessageHandler == null)
        {
            _gmailFilterService = _gmailService;
        }
        else
        {
            _googleProviderFeatureHttpClient = new HttpClient(providerFeatureMessageHandler, disposeHandler: true);
            _gmailFilterService = new GmailService(_googleProviderFeatureHttpClient);
        }
        _peopleService = new PeopleServiceService(_googleHttpClient);
        _calendarService = new GoogleCalendarService(_googleHttpClient);
        _driveService = new DriveService(_googleHttpClient);

        _gmailChangeProcessor = gmailChangeProcessor;
        _gmailSynchronizerErrorHandlerFactory = gmailSynchronizerErrorHandlerFactory;
        _mailFilterExecutor = mailFilterExecutor;
        _contactService = contactService;
        _contactPictureFileService = contactPictureFileService;
        _taskService = taskService;
        _googleTasksClient = new GoogleTasksClient(_googleHttpClient);
    }

    protected override Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
        => !Account.IsContactAccessGranted
            ? _localContactSynchronizer.ExecuteRequestsAsync(requests, cancellationToken)
            : ExecuteGmailContactRequestsAsync(requests, cancellationToken);

    protected override Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
        => !Account.IsContactAccessGranted
            ? _localContactSynchronizer.SynchronizeAsync(options, cancellationToken)
            : SynchronizeGmailContactsAsync(options, cancellationToken);

    protected override async Task ExecuteTaskRequestsInternalAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
    {
        if (!Account.IsTaskAccessGranted || _taskService is null)
        {
            await _localTaskSynchronizer.ExecuteRequestsAsync(requests, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await ExecuteGoogleTaskRequestsAsync(requests, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403)
        {
            await MarkTaskAuthorizationRequiredAsync().ConfigureAwait(false);
            throw new AuthenticationAttentionException(Account);
        }
    }

    protected override async Task<TaskSynchronizationResult> SynchronizeTasksInternalAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (!Account.IsTaskAccessGranted || _taskService is null)
            return await _localTaskSynchronizer.SynchronizeAsync(options, cancellationToken).ConfigureAwait(false);

        try
        {
            return await SynchronizeGoogleTasksAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403)
        {
            await MarkTaskAuthorizationRequiredAsync().ConfigureAwait(false);
            throw new AuthenticationAttentionException(Account);
        }
    }

    private async Task MarkTaskAuthorizationRequiredAsync()
    {
        Account.IsTaskReauthorizationRequired = true;
        if (_taskService is not null)
            await _taskService.MarkTaskListsReadOnlyAsync(Account.Id, TaskSourceKind.Gmail).ConfigureAwait(false);
    }

    private async Task ExecuteGoogleTaskRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (request.Operation)
            {
                case TaskSynchronizerOperation.CreateList:
                    {
                        var localList = await _taskService.GetTaskListAsync(request.TaskListId ?? Guid.Empty).ConfigureAwait(false);
                        if (localList is null)
                            continue;
                        var remote = await _googleTasksClient.CreateTaskListAsync(localList.Title, cancellationToken).ConfigureAwait(false);
                        await _taskService.CompleteListMutationAsync(localList.Id, MapGoogleList(remote), false).ConfigureAwait(false);
                        break;
                    }
                case TaskSynchronizerOperation.UpdateList:
                    {
                        var localList = await _taskService.GetTaskListAsync(request.TaskListId ?? Guid.Empty).ConfigureAwait(false);
                        if (localList?.RemoteId is null)
                            continue;
                        try
                        {
                            var remote = await _googleTasksClient.UpdateTaskListAsync(localList.RemoteId, localList.Title, localList.RemoteVersion, cancellationToken).ConfigureAwait(false);
                            await _taskService.CompleteListMutationAsync(localList.Id, MapGoogleList(remote), false).ConfigureAwait(false);
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed)
                        {
                            var remote = await _googleTasksClient.GetTaskListAsync(localList.RemoteId, cancellationToken).ConfigureAwait(false);
                            await _taskService.CompleteListMutationAsync(localList.Id, MapGoogleList(remote), false).ConfigureAwait(false);
                        }
                        break;
                    }
                case TaskSynchronizerOperation.DeleteList:
                    {
                        var localList = await _taskService.GetTaskListAsync(request.TaskListId ?? Guid.Empty).ConfigureAwait(false);
                        if (localList?.RemoteId is null)
                            continue;
                        try
                        {
                            await _googleTasksClient.DeleteTaskListAsync(localList.RemoteId, localList.RemoteVersion, cancellationToken).ConfigureAwait(false);
                            await _taskService.CompleteListMutationAsync(localList.Id, null, true).ConfigureAwait(false);
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed)
                        {
                            var remote = await _googleTasksClient.GetTaskListAsync(localList.RemoteId, cancellationToken).ConfigureAwait(false);
                            await _taskService.CompleteListMutationAsync(localList.Id, MapGoogleList(remote), false).ConfigureAwait(false);
                        }
                        break;
                    }
                case TaskSynchronizerOperation.CreateTask:
                case TaskSynchronizerOperation.UpdateTask:
                case TaskSynchronizerOperation.DeleteTask:
                    await ExecuteGoogleTaskMutationAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.CreateStep:
                case TaskSynchronizerOperation.UpdateStep:
                case TaskSynchronizerOperation.DeleteStep:
                    await ExecuteGoogleStepMutationAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
            }

            MarkTaskRequestProcessed(request);
        }
    }

    private async Task ExecuteGoogleTaskMutationAsync(ITaskActionRequest request, CancellationToken cancellationToken)
    {
        var localTask = await _taskService.GetTaskAsync(request.TaskId ?? Guid.Empty).ConfigureAwait(false);
        if (localTask is null)
            return;
        var list = await _taskService.GetTaskListAsync(localTask.TaskListId).ConfigureAwait(false);
        if (list?.RemoteId is null)
            return;
        try
        {
            if (request.Operation == TaskSynchronizerOperation.DeleteTask)
            {
                if (localTask.RemoteId is not null)
                    await _googleTasksClient.DeleteTaskAsync(list.RemoteId, localTask.RemoteId, localTask.RemoteVersion, cancellationToken).ConfigureAwait(false);
                await _taskService.CompleteTaskMutationAsync(localTask.Id, null, true).ConfigureAwait(false);
                return;
            }

            var payload = BuildGoogleTask(localTask);
            var remote = request.Operation == TaskSynchronizerOperation.CreateTask || localTask.RemoteId is null
                ? await _googleTasksClient.CreateTaskAsync(list.RemoteId, payload, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _googleTasksClient.UpdateTaskAsync(list.RemoteId, localTask.RemoteId, payload, localTask.RemoteVersion, cancellationToken).ConfigureAwait(false);
            await _taskService.CompleteTaskMutationAsync(localTask.Id, MapGoogleTask(remote, list), false).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when ((ex.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed) && localTask.RemoteId is not null)
        {
            var remote = await _googleTasksClient.GetTaskAsync(list.RemoteId, localTask.RemoteId, cancellationToken).ConfigureAwait(false);
            await _taskService.CompleteTaskMutationAsync(localTask.Id, MapGoogleTask(remote, list), false).ConfigureAwait(false);
        }
    }

    private async Task ExecuteGoogleStepMutationAsync(ITaskActionRequest request, CancellationToken cancellationToken)
    {
        var task = await _taskService.GetTaskAsync(request.TaskId ?? Guid.Empty).ConfigureAwait(false);
        var list = task is null ? null : await _taskService.GetTaskListAsync(task.TaskListId).ConfigureAwait(false);
        var requestedStep = (request as TaskActionRequest)?.Step;
        var step = task?.Steps.FirstOrDefault(item => item.Id == requestedStep?.Id) ?? requestedStep;
        if (task is null || list?.RemoteId is null || task.RemoteId is null || step is null)
            return;

        try
        {
            if (request.Operation == TaskSynchronizerOperation.DeleteStep)
            {
                if (step.RemoteId is not null)
                    await _googleTasksClient.DeleteTaskAsync(list.RemoteId, step.RemoteId, step.RemoteVersion, cancellationToken).ConfigureAwait(false);
                await _taskService.CompleteStepMutationAsync(step.Id, null, true).ConfigureAwait(false);
                return;
            }

            var payload = BuildGoogleStep(step);
            var remote = request.Operation == TaskSynchronizerOperation.CreateStep || step.RemoteId is null
                ? await _googleTasksClient.CreateTaskAsync(list.RemoteId, payload, task.RemoteId, cancellationToken).ConfigureAwait(false)
                : await _googleTasksClient.UpdateTaskAsync(list.RemoteId, step.RemoteId, payload, step.RemoteVersion, cancellationToken).ConfigureAwait(false);
            await _taskService.CompleteStepMutationAsync(step.Id, new AccountTaskStep
            {
                RemoteId = remote.Id,
                RemoteVersion = remote.Etag
            }, false).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when ((ex.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed) && step.RemoteId is not null)
        {
            var remote = await _googleTasksClient.GetTaskAsync(list.RemoteId, step.RemoteId, cancellationToken).ConfigureAwait(false);
            await _taskService.CompleteStepMutationAsync(step.Id, new AccountTaskStep
            {
                RemoteId = remote.Id,
                RemoteVersion = remote.Etag,
                Title = remote.Title,
                IsCompleted = string.Equals(remote.Status, "completed", StringComparison.OrdinalIgnoreCase)
            }, false).ConfigureAwait(false);
        }
    }

    private async Task<TaskSynchronizationResult> SynchronizeGoogleTasksAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var syncStart = DateTimeOffset.UtcNow;
        var existingLists = (await _taskService.GetTaskListsAsync(Account.Id).ConfigureAwait(false))
            .Where(list => list.SourceKind == TaskSourceKind.Gmail)
            .ToList();
        var remoteLists = await _googleTasksClient.GetTaskListsAsync(cancellationToken).ConfigureAwait(false);
        var remoteIds = remoteLists.Select(list => list.Id).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var deleted = 0;
        foreach (var stale in existingLists.Where(list => list.RemoteId is not null && !remoteIds.Contains(list.RemoteId)))
        {
            await _taskService.RemoveTaskListAsync(stale.Id).ConfigureAwait(false);
            deleted++;
        }

        var changed = 0;
        foreach (var remoteList in remoteLists.Where(list => !string.IsNullOrWhiteSpace(list.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = existingLists.FirstOrDefault(list => list.RemoteId == remoteList.Id);
            var list = await _taskService.UpsertRemoteTaskListAsync(new AccountTaskList
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                MailAccountId = Account.Id,
                SourceKind = TaskSourceKind.Gmail,
                RemoteId = remoteList.Id,
                RemoteVersion = remoteList.Etag,
                Title = remoteList.Title ?? "Tasks",
                IsDefault = false,
                IsReadOnly = false,
                TaskDeltaLink = null,
                WatermarkUtc = existing?.WatermarkUtc
            }).ConfigureAwait(false);
            var taskResult = await SynchronizeGoogleTaskListAsync(list, options.Type is TaskSynchronizationType.Full or TaskSynchronizationType.Strict, syncStart, cancellationToken).ConfigureAwait(false);
            changed += taskResult.ChangedCount;
            deleted += taskResult.DeletedCount;
        }

        return TaskSynchronizationResult.Completed(remoteLists.Count, changed, deleted);
    }

    private async Task<TaskSynchronizationResult> SynchronizeGoogleTaskListAsync(AccountTaskList list, bool full, DateTimeOffset syncStart, CancellationToken cancellationToken)
    {
        var remoteTasks = await _googleTasksClient.GetTasksAsync(list.RemoteId, full ? null : list.WatermarkUtc is DateTime watermark ? new DateTimeOffset(watermark, TimeSpan.Zero) : null, cancellationToken).ConfigureAwait(false);
        var current = await _taskService.GetTasksAsync(listId: list.Id).ConfigureAwait(false);
        var byRemoteId = full
            ? new Dictionary<string, AccountTask>(StringComparer.Ordinal)
            : current.Where(task => task.RemoteId is not null).ToDictionary(task => task.RemoteId, StringComparer.Ordinal);
        var childrenByParent = remoteTasks.Where(task => !string.IsNullOrWhiteSpace(task.Parent)).GroupBy(task => task.Parent).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        var changed = 0;

        foreach (var remote in remoteTasks)
        {
            if (remote?.Id is null)
                continue;
            if (remote.Deleted)
            {
                deletedIds.Add(remote.Id);
                if (string.IsNullOrWhiteSpace(remote.Parent))
                    byRemoteId.Remove(remote.Id);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(remote.Parent))
                continue;

            var children = childrenByParent.TryGetValue(remote.Id, out var remoteChildren)
                ? remoteChildren
                : [];
            var mapped = MapGoogleTask(remote, list, children);
            var hasExistingParent = byRemoteId.TryGetValue(remote.Id, out var existingParent);
            if (!full && hasExistingParent && children.Count == 0)
                mapped.Steps = existingParent.Steps ?? [];
            else if (!full && hasExistingParent)
                mapped.Steps = MergeGoogleChildren(existingParent, children);

            byRemoteId[remote.Id] = mapped;
            changed++;
        }

        // Google returns changed child tasks independently from their parent task when
        // updatedMin is used. Merge those children into the cached parent instead of
        // dropping them as non-root tasks.
        if (!full)
        {
            var changedRootIds = remoteTasks
                .Where(task => !task.Deleted && string.IsNullOrWhiteSpace(task.Parent))
                .Select(task => task.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var remoteChild in remoteTasks.Where(task => !task.Deleted && !string.IsNullOrWhiteSpace(task.Parent)))
            {
                if (byRemoteId.TryGetValue(remoteChild.Parent, out var parent))
                {
                    parent.Steps = MergeGoogleChildren(parent, [remoteChild]);
                    if (!changedRootIds.Contains(remoteChild.Parent))
                        changed++;
                }
            }
        }

        foreach (var deletedId in deletedIds)
        {
            foreach (var task in byRemoteId.Values)
                task.Steps = task.Steps.Where(step => step.RemoteId != deletedId).ToList();
        }

        await _taskService.ReplaceListAsync(list.Id, byRemoteId.Values.ToList(), null, syncStart.UtcDateTime).ConfigureAwait(false);
        return TaskSynchronizationResult.Completed(byRemoteId.Count, changed, deletedIds.Count);
    }

    private static AccountTaskList MapGoogleList(GoogleTaskList remote)
        => new()
        {
            RemoteId = remote?.Id,
            RemoteVersion = remote?.Etag,
            Title = remote?.Title ?? "Tasks",
            SourceKind = TaskSourceKind.Gmail
        };

    private static AccountTask MapGoogleTask(GoogleTask remote, AccountTaskList list, IReadOnlyList<GoogleTask> children = null)
    {
        var task = new AccountTask
        {
            MailAccountId = list.MailAccountId,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = remote.Id,
            RemoteVersion = remote.Etag,
            Title = remote.Title ?? string.Empty,
            Notes = remote.Notes,
            DueDate = remote.Due?.Date,
            IsCompleted = string.Equals(remote.Status, "completed", StringComparison.OrdinalIgnoreCase),
            CompletedAtUtc = remote.Completed?.UtcDateTime,
            RemoteOrder = remote.Position,
            PendingMutation = TaskPendingMutation.None
        };
        task.Steps = (children ?? [])
            .OrderBy(child => child.Position, StringComparer.Ordinal)
            .Select((child, index) => MapGoogleStep(child, task, index))
            .ToList();
        return task;
    }

    private static List<AccountTaskStep> MergeGoogleChildren(AccountTask parent, IReadOnlyList<GoogleTask> children)
    {
        var merged = (parent?.Steps ?? [])
            .Where(step => !string.IsNullOrWhiteSpace(step.RemoteId))
            .ToDictionary(step => step.RemoteId, StringComparer.Ordinal);
        var nextOrder = merged.Count == 0 ? 0 : merged.Values.Max(step => step.Order) + 1;

        foreach (var child in children ?? [])
        {
            if (child?.Id is null)
                continue;

            var order = merged.TryGetValue(child.Id, out var existing) ? existing.Order : nextOrder++;
            merged[child.Id] = MapGoogleStep(child, parent, order);
        }

        return merged.Values.OrderBy(step => step.Order).ToList();
    }

    private static AccountTaskStep MapGoogleStep(GoogleTask remote, AccountTask parent, int order)
        => new()
        {
            TaskId = parent.Id,
            MailAccountId = parent.MailAccountId,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = remote.Id,
            RemoteVersion = remote.Etag,
            Title = remote.Title ?? string.Empty,
            IsCompleted = string.Equals(remote.Status, "completed", StringComparison.OrdinalIgnoreCase),
            Order = order,
            PendingMutation = TaskPendingMutation.None
        };

    private static GoogleTask BuildGoogleTask(AccountTask task)
        => new()
        {
            Title = task.Title,
            Notes = task.Notes,
            Status = task.IsCompleted ? "completed" : "needsAction",
            Due = task.DueDate is DateTime due ? new DateTimeOffset(due, TimeSpan.Zero) : null,
            Parent = null
        };

    private static GoogleTask BuildGoogleStep(AccountTaskStep step)
        => new()
        {
            Title = step.Title,
            Status = step.IsCompleted ? "completed" : "needsAction"
        };

    private const string GoogleContactFields = "names,emailAddresses,phoneNumbers,addresses,organizations,birthdays,nicknames,fileAses,biographies,urls,imClients,relations,photos,metadata";

    private async Task<ContactSynchronizationResult> SynchronizeGmailContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        if (_contactService is null)
            return ContactSynchronizationResult.Failed(new InvalidOperationException("Contact storage is unavailable."));

        var book = await _contactService.GetOrCreateProviderAddressBookAsync(
            Account.Id, ContactSourceKind.Gmail, "people/me/connections", Account.Name, true).ConfigureAwait(false);
        var isFull = options.Type == ContactSynchronizationType.Full || string.IsNullOrWhiteSpace(book.DeltaToken);
        var downloaded = new List<global::Google.Apis.PeopleService.v1.Data.Person>();
        string pageToken = null;
        string nextSyncToken = null;

        try
        {
            do
            {
                var request = _peopleService.Connections.List("people/me");
                request.PersonFields = GoogleContactFields;
                request.PageSize = 1000;
                request.PageToken = pageToken;
                request.SyncToken = isFull ? null : book.DeltaToken;
                var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                downloaded.AddRange(response?.Connections ?? []);
                pageToken = response?.NextPageToken;
                nextSyncToken = response?.NextSyncToken ?? nextSyncToken;
            }
            while (!string.IsNullOrWhiteSpace(pageToken));
        }
        catch (GoogleApiException ex) when (!isFull && (int)ex.HttpStatusCode == 410)
        {
            return await SynchronizeGmailContactsAsync(new ContactSynchronizationOptions
            {
                AccountId = options.AccountId,
                AddressBookId = book.Id,
                Type = ContactSynchronizationType.Full
            }, cancellationToken).ConfigureAwait(false);
        }

        var active = downloaded.Where(person => person.Metadata?.Deleted != true).Select(person => MapGoogleContact(person, book)).ToList();
        var existingContacts = await _contactService.GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false);
        await DownloadGoogleContactPhotosAsync(active, existingContacts, cancellationToken).ConfigureAwait(false);
        var deletedIds = downloaded.Where(person => person.Metadata?.Deleted == true).Select(person => person.ResourceName).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (isFull)
            await _contactService.ReplaceAddressBookAsync(book.Id, active, nextSyncToken).ConfigureAwait(false);
        else
            await _contactService.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(active, deletedIds, nextSyncToken), commitDeltaToken: true).ConfigureAwait(false);

        return ContactSynchronizationResult.Completed(active.Count, active.Count, deletedIds.Count);
    }

    private async Task DownloadGoogleContactPhotosAsync(
        IReadOnlyList<AccountContact> contacts,
        IReadOnlyList<AccountContact> existingContacts,
        CancellationToken cancellationToken)
    {
        if (_contactPictureFileService is null)
            return;

        var existingByRemoteId = existingContacts
            .Where(contact => !string.IsNullOrWhiteSpace(contact.RemoteId))
            .ToDictionary(contact => contact.RemoteId, StringComparer.Ordinal);
        var pending = new List<AccountContact>();

        foreach (var contact in contacts.Where(contact => !string.IsNullOrWhiteSpace(contact.RemotePhotoKey)))
        {
            if (existingByRemoteId.TryGetValue(contact.RemoteId ?? string.Empty, out var existing) &&
                existing.ContactPictureFileId.HasValue &&
                string.Equals(existing.RemotePhotoKey, contact.RemotePhotoKey, StringComparison.Ordinal))
            {
                contact.ContactPictureFileId = existing.ContactPictureFileId;
                continue;
            }

            pending.Add(contact);
        }

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (contact, token) =>
            {
                try
                {
                    var bytes = await _googleHttpClient.GetByteArrayAsync(contact.RemotePhotoKey, token).ConfigureAwait(false);
                    if (bytes?.Length > 0)
                        contact.ContactPictureFileId = await _contactPictureFileService.SaveContactPictureAsync(bytes).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.Warning(ex, "Failed to cache Gmail contact photo {RemoteId}.", contact.RemoteId); }
            }).ConfigureAwait(false);
    }

    private async Task ExecuteGmailContactRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken)
    {
        if (_contactService is null)
            throw new InvalidOperationException("Contact storage is unavailable.");

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = await _contactService.GetContactAsync(request.LocalContactId).ConfigureAwait(false);
            if (local is null && request.Operation != ContactSynchronizerOperation.Delete)
                continue;

            try
            {
                switch (request.Operation)
                {
                    case ContactSynchronizerOperation.Create:
                        {
                            var create = _peopleService.People.CreateContact(MapToGoogleContact(local));
                            create.PersonFields = GoogleContactFields;
                            var created = await create.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                            var book = (await _contactService.GetAddressBooksAsync(Account.Id).ConfigureAwait(false)).First(item => item.Id == request.AddressBookId);
                            await _contactService.CompleteMutationAsync(local.Id, MapGoogleContact(created, book), false).ConfigureAwait(false);
                            break;
                        }
                    case ContactSynchronizerOperation.Update:
                        {
                            var currentRequest = _peopleService.People.Get(local.RemoteId);
                            currentRequest.PersonFields = GoogleContactFields;
                            var current = await currentRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                            MergeCommonFields(current, local);
                            var updated = await _peopleService.People.UpdateContact(local.RemoteId, current).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                            var book = (await _contactService.GetAddressBooksAsync(Account.Id).ConfigureAwait(false)).First(item => item.Id == request.AddressBookId);
                            await _contactService.CompleteMutationAsync(local.Id, MapGoogleContact(updated, book), false).ConfigureAwait(false);
                            break;
                        }
                    case ContactSynchronizerOperation.Delete:
                        if (!string.IsNullOrWhiteSpace(local?.RemoteId))
                            await _peopleService.People.DeleteContact(local.RemoteId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        if (local is not null)
                            await _contactService.CompleteMutationAsync(local.Id, null, true).ConfigureAwait(false);
                        break;
                    case ContactSynchronizerOperation.SetPhoto:
                        await UpdateGoogleContactPhotoAsync(local.RemoteId, request.Photo, delete: false, cancellationToken).ConfigureAwait(false);
                        await _contactService.CompleteMutationAsync(local.Id, null, false).ConfigureAwait(false);
                        break;
                    case ContactSynchronizerOperation.DeletePhoto:
                        await UpdateGoogleContactPhotoAsync(local.RemoteId, null, delete: true, cancellationToken).ConfigureAwait(false);
                        await _contactService.CompleteMutationAsync(local.Id, null, false).ConfigureAwait(false);
                        break;
                }
            }
            catch (GoogleApiException ex) when ((int)ex.HttpStatusCode is 400 or 409 or 412)
            {
                if (local is null || string.IsNullOrWhiteSpace(local.RemoteId))
                    continue;
                var fetch = _peopleService.People.Get(local.RemoteId);
                fetch.PersonFields = GoogleContactFields;
                var remote = await fetch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                var book = (await _contactService.GetAddressBooksAsync(Account.Id).ConfigureAwait(false)).First(item => item.Id == request.AddressBookId);
                await _contactService.CompleteMutationAsync(local.Id, MapGoogleContact(remote, book), false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Gmail contact request {Operation} failed for contact {ContactId}.",
                    request.Operation, request.LocalContactId);
            }
        }
    }

    private async Task UpdateGoogleContactPhotoAsync(string remoteId, byte[] photo, bool delete, CancellationToken cancellationToken)
    {
        var escaped = Uri.EscapeDataString(remoteId).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        var verb = delete ? "deleteContactPhoto" : "updateContactPhoto";
        using var message = new HttpRequestMessage(HttpMethod.Patch, $"https://people.googleapis.com/v1/{escaped}:{verb}");
        if (!delete)
            message.Content = GoogleJsonContent.Create(new global::Google.Apis.PeopleService.v1.Data.UpdateContactPhotoRequest { PhotoBytes = Convert.ToBase64String(photo ?? []) }, GoogleApiJsonContext.Default.UpdateContactPhotoRequest);
        using var response = await _googleHttpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await GoogleApiErrorParser.ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private AccountContact MapGoogleContact(global::Google.Apis.PeopleService.v1.Data.Person person, ContactAddressBook book)
    {
        var name = person.Names?.FirstOrDefault(item => item.Metadata?.Primary == true) ?? person.Names?.FirstOrDefault();
        var organization = person.Organizations?.FirstOrDefault(item => item.Metadata?.Primary == true) ?? person.Organizations?.FirstOrDefault();
        var birthday = person.Birthdays?.FirstOrDefault()?.Date;
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = Account.Id,
            AddressBookId = book.Id,
            SourceKind = ContactSourceKind.Gmail,
            RemoteId = person.ResourceName,
            RemoteVersion = person.Etag,
            RemotePhotoKey = person.Photos?.FirstOrDefault(item => item.Metadata?.Primary == true)?.Url,
            DisplayName = name?.DisplayName,
            HonorificPrefix = name?.HonorificPrefix,
            GivenName = name?.GivenName,
            MiddleName = name?.MiddleName,
            Surname = name?.FamilyName,
            HonorificSuffix = name?.HonorificSuffix,
            Nickname = person.Nicknames?.FirstOrDefault()?.Value,
            FileAs = person.FileAses?.FirstOrDefault()?.Value,
            CompanyName = organization?.Name,
            Department = organization?.Department,
            JobTitle = organization?.Title,
            OfficeLocation = organization?.Location,
            Profession = organization?.JobDescription,
            BirthdayYear = birthday?.Year,
            BirthdayMonth = birthday?.Month,
            BirthdayDay = birthday?.Day,
            Notes = person.Biographies?.FirstOrDefault()?.Value,
            Website = person.Urls?.FirstOrDefault()?.Value
        };
        contact.EmailAddresses = person.EmailAddresses?.Take(3).Select((item, index) => new ContactEmailAddress { Id = Guid.NewGuid(), ContactId = contact.Id, Address = item.Value, NormalizedAddress = ContactEmailAddress.Normalize(item.Value), Label = item.Type, Order = index, IsPrimary = item.Metadata?.Primary == true || index == 0 }).ToList() ?? [];
        contact.PhoneNumbers = person.PhoneNumbers?.Select((item, index) => new ContactPhoneNumber { Id = Guid.NewGuid(), ContactId = contact.Id, Number = item.Value, Kind = MapPhoneKind(item.Type), Order = index, IsPrimary = item.Metadata?.Primary == true || index == 0 }).ToList() ?? [];
        contact.PostalAddresses = person.Addresses?.GroupBy(item => MapAddressKind(item.Type)).Select(group => group.First()).Take(3).Select(item => new ContactPostalAddress { Id = Guid.NewGuid(), ContactId = contact.Id, Kind = MapAddressKind(item.Type), PostOfficeBox = item.PoBox, Street = item.StreetAddress, City = item.City, Region = item.Region, PostalCode = item.PostalCode, Country = item.Country }).ToList() ?? [];
        contact.ImAddresses = person.ImClients?.Select((item, index) => new ContactImAddress { Id = Guid.NewGuid(), ContactId = contact.Id, Address = item.Username, Protocol = item.Protocol, Order = index }).ToList() ?? [];
        contact.Relations = person.Relations?.Where(item => TryMapRelation(item.Type, out _)).Select((item, index) => new ContactRelation { Id = Guid.NewGuid(), ContactId = contact.Id, Kind = MapRelation(item.Type), Name = item.Person, Order = index }).ToList() ?? [];
        return contact;
    }

    private static global::Google.Apis.PeopleService.v1.Data.Person MapToGoogleContact(AccountContact contact)
    {
        var person = new global::Google.Apis.PeopleService.v1.Data.Person { ResourceName = contact.RemoteId, Etag = contact.RemoteVersion };
        MergeCommonFields(person, contact);
        return person;
    }

    private static void MergeCommonFields(global::Google.Apis.PeopleService.v1.Data.Person person, AccountContact contact)
    {
        person.Names = [new() { DisplayName = contact.DisplayName, HonorificPrefix = contact.HonorificPrefix, GivenName = contact.GivenName, MiddleName = contact.MiddleName, FamilyName = contact.Surname, HonorificSuffix = contact.HonorificSuffix }];
        person.EmailAddresses = contact.EmailAddresses.Select(item => new global::Google.Apis.PeopleService.v1.Data.EmailAddress { Value = item.Address, Type = item.Label }).ToList();
        person.PhoneNumbers = contact.PhoneNumbers.Select(item => new global::Google.Apis.PeopleService.v1.Data.PhoneNumber { Value = item.Number, Type = item.Kind.ToString().ToLowerInvariant() }).ToList();
        person.Addresses = contact.PostalAddresses.Select(item => new global::Google.Apis.PeopleService.v1.Data.Address { Type = item.Kind == ContactPostalAddressKind.Business ? "work" : item.Kind.ToString().ToLowerInvariant(), PoBox = item.PostOfficeBox, StreetAddress = item.Street, City = item.City, Region = item.Region, PostalCode = item.PostalCode, Country = item.Country }).ToList();
        person.Organizations = [new() { Name = contact.CompanyName, Department = contact.Department, Title = contact.JobTitle, Location = contact.OfficeLocation, JobDescription = contact.Profession }];
        person.Birthdays = contact.BirthdayMonth.HasValue && contact.BirthdayDay.HasValue ? [new() { Date = new() { Year = contact.BirthdayYear, Month = contact.BirthdayMonth, Day = contact.BirthdayDay } }] : [];
        person.Nicknames = string.IsNullOrWhiteSpace(contact.Nickname) ? [] : [new() { Value = contact.Nickname }];
        person.FileAses = string.IsNullOrWhiteSpace(contact.FileAs) ? [] : [new() { Value = contact.FileAs }];
        person.Biographies = string.IsNullOrWhiteSpace(contact.Notes) ? [] : [new() { Value = contact.Notes, ContentType = "TEXT_PLAIN" }];
        person.Urls = string.IsNullOrWhiteSpace(contact.Website) ? [] : [new() { Value = contact.Website }];
        person.ImClients = contact.ImAddresses.Select(item => new global::Google.Apis.PeopleService.v1.Data.ImClient { Username = item.Address, Protocol = item.Protocol }).ToList();
        person.Relations = contact.Relations.Select(item => new global::Google.Apis.PeopleService.v1.Data.Relation { Person = item.Name, Type = item.Kind.ToString().ToLowerInvariant() }).ToList();
    }

    private static ContactPhoneKind MapPhoneKind(string value) => value?.ToLowerInvariant() switch { "work" => ContactPhoneKind.Work, "mobile" => ContactPhoneKind.Mobile, _ => ContactPhoneKind.Home };
    private static ContactPostalAddressKind MapAddressKind(string value) => value?.ToLowerInvariant() switch { "work" => ContactPostalAddressKind.Business, "other" => ContactPostalAddressKind.Other, _ => ContactPostalAddressKind.Home };
    private static bool TryMapRelation(string value, out ContactRelationKind kind) { kind = MapRelation(value); return value?.ToLowerInvariant() is "manager" or "assistant" or "spouse" or "child"; }
    private static ContactRelationKind MapRelation(string value) => value?.ToLowerInvariant() switch { "manager" => ContactRelationKind.Manager, "assistant" => ContactRelationKind.Assistant, "spouse" => ContactRelationKind.Spouse, _ => ContactRelationKind.Child };

    public override async Task<ProfileInformation> GetProfileInformationAsync()
    {
        var profileRequest = _peopleService.People.Get("people/me");
        profileRequest.PersonFields = "names,photos,emailAddresses";

        var profilePictureResult = ProfilePictureFetchResult.FetchFailed;

        global::Google.Apis.PeopleService.v1.Data.Person userProfile = null;

        try
        {
            userProfile = await profileRequest.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch Gmail People profile for {Name}. Falling back to Gmail profile APIs.", Account.Name);
        }

        var primarySendAs = await GetPrimarySendAsAsync().ConfigureAwait(false);
        var gmailProfileAddress = await GetGmailProfileAddressAsync().ConfigureAwait(false);

        var address = GetPrimaryProfileEmail(userProfile)
            ?? primarySendAs?.SendAsEmail
            ?? gmailProfileAddress
            ?? Account.Address;

        var senderName = userProfile.Names?
            .FirstOrDefault(name => name?.Metadata?.Primary == true)?.DisplayName;

        senderName = string.IsNullOrWhiteSpace(senderName)
            ? userProfile.Names?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name?.DisplayName))?.DisplayName
            : senderName;

        senderName = string.IsNullOrWhiteSpace(senderName)
            ? primarySendAs?.DisplayName
            : senderName;

        senderName = string.IsNullOrWhiteSpace(senderName)
            ? Account.SenderName
            : senderName;

        senderName = string.IsNullOrWhiteSpace(senderName)
            ? GetDisplayNameFallback(address)
            : senderName;

        var profilePicture = userProfile?.Photos?
            .FirstOrDefault(photo => !string.IsNullOrWhiteSpace(photo?.Url))?
            .Url ?? string.Empty;

        if (!string.IsNullOrEmpty(profilePicture))
        {
            try
            {
                var pictureBytes = await GetProfilePictureAsync(_googleHttpClient, profilePicture).ConfigureAwait(false);
                profilePictureResult = ProfilePictureFetchResult.Downloaded(pictureBytes);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to fetch Gmail profile picture for {Name}", Account.Name);
            }
        }
        else if (userProfile != null)
        {
            profilePictureResult = ProfilePictureFetchResult.ConfirmedAbsent;
        }

        return new ProfileInformation(senderName, profilePictureResult, address);
    }

    private async Task<SendAs> GetPrimarySendAsAsync()
    {
        try
        {
            var sendAsListResponse = await _gmailService.Users.Settings.SendAs.List("me").ExecuteAsync().ConfigureAwait(false);

            return sendAsListResponse?.SendAs?
                .FirstOrDefault(sendAs => sendAs?.IsPrimary == true)
                ?? sendAsListResponse?.SendAs?.FirstOrDefault(sendAs => sendAs?.IsDefault == true)
                ?? sendAsListResponse?.SendAs?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch Gmail send-as profile fallback for {Name}", Account.Name);
            return null;
        }
    }

    private async Task<string> GetGmailProfileAddressAsync()
    {
        try
        {
            var profile = await _gmailService.Users.GetProfile("me").ExecuteAsync().ConfigureAwait(false);
            return profile?.EmailAddress;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch Gmail profile address fallback for {Name}", Account.Name);
            return null;
        }
    }

    private static string GetPrimaryProfileEmail(global::Google.Apis.PeopleService.v1.Data.Person userProfile)
        => userProfile?.EmailAddresses?
            .FirstOrDefault(email => email?.Metadata?.Primary == true)?.Value
            ?? userProfile?.EmailAddresses?.FirstOrDefault(email => !string.IsNullOrWhiteSpace(email?.Value))?.Value;

    private static string GetDisplayNameFallback(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return string.Empty;

        var atIndex = address.IndexOf('@');
        return atIndex > 0 ? address[..atIndex] : address;
    }

    protected override async Task SynchronizeAliasesAsync()
    {
        var sendAsListRequest = _gmailService.Users.Settings.SendAs.List("me");
        var sendAsListResponse = await sendAsListRequest.ExecuteAsync();
        var remoteAliases = sendAsListResponse.GetRemoteAliases();

        await _gmailChangeProcessor.UpdateRemoteAliasInformationAsync(Account, remoteAliases).ConfigureAwait(false);
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        _logger.Information("Internal mail synchronization started for {Name}", Account.Name);

        var downloadedMessageIds = new List<string>();
        var shouldRunLocalFilters = false;
        var folderResults = new List<FolderSyncResult>();

        try
        {
            _isFolderStructureChanged = false;

            // Make sure that virtual archive folder exists before all.
            if (!archiveFolderId.HasValue)
                await InitializeArchiveFolderAsync().ConfigureAwait(false);

            // Gmail must always synchronize folders before because it doesn't have a per-folder sync.
            _logger.Information("Synchronizing folders for {Name}", Account.Name);
            UpdateSyncProgress(0, 0, "Synchronizing folders...");

            try
            {
                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException googleException) when (googleException.Message.Contains("Mail service not enabled"))
            {
                throw new GmailServiceDisabledException();
            }

            if (_isFolderStructureChanged)
            {
                WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
            }

            _logger.Information("Synchronizing folders for {Name} is completed", Account.Name);
            UpdateSyncProgress(0, 0, "Folders synchronized");

            // Stop synchronization at this point if type is only folder metadata sync.
            if (options.Type == MailSynchronizationType.FoldersOnly) return MailSynchronizationResult.Empty;

            cancellationToken.ThrowIfCancellationRequested();

            bool isInitialSync = string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier);

            _logger.Debug("Is initial synchronization: {IsInitialSync}", isInitialSync);

            if (isInitialSync)
            {
                // INITIAL SYNC: Download all messages globally (not per-folder) to avoid duplicates.
                // Gmail messages can have multiple labels, so per-folder download would fetch same message multiple times.
                downloadedMessageIds = await PerformInitialSyncAsync(cancellationToken).ConfigureAwait(false);

                // Set the history ID to the latest value after initial sync
                UpdateSyncProgress(0, 0, "Finalizing synchronization...");
                var profile = await _gmailService.Users.GetProfile("me").ExecuteAsync(cancellationToken);
                if (profile.HistoryId.HasValue)
                {
                    await UpdateAccountSyncIdentifierAsync(profile.HistoryId.Value).ConfigureAwait(false);
                    _logger.Information("Initial sync completed. Set history ID to {HistoryId}", profile.HistoryId.Value);
                }

                // Create successful folder results for all folders
                var allFolders = await _gmailChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);
                foreach (var folder in allFolders.Where(f => f.RemoteFolderId != ServiceConstants.ARCHIVE_LABEL_ID))
                {
                    folderResults.Add(FolderSyncResult.Successful(folder.Id, folder.FolderName, 0));
                }
            }
            else
            {
                // INCREMENTAL SYNC: Use ONLY History API - no per-folder downloads.
                // This is the proper Gmail sync strategy as recommended by Google.
                UpdateSyncProgress(0, 0, "Synchronizing changes...");
                var deltaResult = await SynchronizeDeltaAsync(options, cancellationToken).ConfigureAwait(false);
                downloadedMessageIds.AddRange(deltaResult.DownloadedMessageIds);

                // If history sync was reset due to expired history ID, we need to do initial sync
                if (deltaResult.RequiresFullResync)
                {
                    _logger.Warning("History ID expired. Performing full resync for {Name}", Account.Name);
                    downloadedMessageIds = await PerformInitialSyncAsync(cancellationToken).ConfigureAwait(false);

                    // Update history ID after full resync
                    var profile = await _gmailService.Users.GetProfile("me").ExecuteAsync(cancellationToken);
                    if (profile.HistoryId.HasValue)
                    {
                        await UpdateAccountSyncIdentifierAsync(profile.HistoryId.Value).ConfigureAwait(false);
                        _logger.Information("Full resync completed. Set history ID to {HistoryId}", profile.HistoryId.Value);
                    }
                }
                else
                {
                    shouldRunLocalFilters = true;
                }

                UpdateSyncProgress(0, 0, "Changes synchronized");

                // Create folder results for incremental sync
                var allFolders = await _gmailChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);
                foreach (var folder in allFolders.Where(f => f.RemoteFolderId != ServiceConstants.ARCHIVE_LABEL_ID))
                {
                    folderResults.Add(FolderSyncResult.Successful(folder.Id, folder.FolderName, 0));
                }
            }

            // Map Gmail Draft resource IDs for all drafts.
            // Gmail's Messages API doesn't expose Draft IDs, so we query the Drafts API separately.
            // This ensures DraftId is correctly set for both Wino-created and externally-created drafts.
            await MapDraftIdsAsync(cancellationToken).ConfigureAwait(false);

            // Keep virtual Archive folder assignments in sync with Gmail "in:archive" query.
            try
            {
                var referenceDateUtc = Account.CreatedAt ?? DateTime.UtcNow;
                var initialSynchronizationCutoffDateUtc = Account.InitialSynchronizationRange.ToCutoffDateUtc(referenceDateUtc);
                await MapArchivedMailsAsync(initialSynchronizationCutoffDateUtc, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to map Gmail archive folder for {Name}", Account.Name);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Synchronization was canceled for {Name}", Account.Name);
            return MailSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Synchronization failed for {Name}", Account.Name);
            return MailSynchronizationResult.Failed(ex);
        }

        // Get all unread new downloaded items for notifications
        var suppressedIds = _mailFilterExecutor == null || !shouldRunLocalFilters
            ? new HashSet<string>(StringComparer.Ordinal)
            : await _mailFilterExecutor.ProcessNewMessagesAsync(Account.Id, downloadedMessageIds, cancellationToken).ConfigureAwait(false);
        var unreadNewItems = await _gmailChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);
        unreadNewItems.RemoveAll(item => suppressedIds.Contains(item.Id));

        return MailSynchronizationResult.CompletedWithFolderResults(unreadNewItems, folderResults);
    }

    /// <summary>
    /// Result of delta synchronization using History API.
    /// </summary>
    private record DeltaSyncResult(List<string> DownloadedMessageIds, bool RequiresFullResync);

    internal static string BuildGmailSearchQuery(string queryText, DateTime? cutoffDateUtc)
    {
        var afterTerm = cutoffDateUtc.HasValue
            ? $"after:{FormatGmailSearchDate(cutoffDateUtc.Value)}"
            : null;

        if (string.IsNullOrWhiteSpace(queryText))
            return afterTerm;

        return string.IsNullOrEmpty(afterTerm)
            ? queryText
            : $"{queryText} {afterTerm}";
    }

    private static string FormatGmailSearchDate(DateTime value)
        => value.ToUniversalTime().ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);

    private static string FormatGoogleCalendarDate(DateTime value)
        => value.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Performs initial synchronization by downloading messages per-folder.
    /// Messages are filtered by the account's configured initial synchronization cutoff date when present,
    /// and duplicates are avoided globally because Gmail messages can have multiple labels.
    /// </summary>
    private async Task<List<string>> PerformInitialSyncAsync(CancellationToken cancellationToken)
    {
        // Track all downloaded message IDs globally to avoid duplicate downloads
        var downloadedMessageIds = new HashSet<string>();
        var referenceDateUtc = Account.CreatedAt ?? DateTime.UtcNow;
        var initialSynchronizationCutoffDateUtc = Account.InitialSynchronizationRange.ToCutoffDateUtc(referenceDateUtc);
        var queryText = BuildGmailSearchQuery(null, initialSynchronizationCutoffDateUtc);

        _logger.Information("Performing initial sync for {Name} - downloading messages per folder", Account.Name);

        try
        {
            // Get all folders to sync (exclude virtual ARCHIVE folder)
            var folders = await _gmailChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var syncableFolders = folders
                .Where(f => f.IsSynchronizationEnabled && f.RemoteFolderId != ServiceConstants.ARCHIVE_LABEL_ID)
                .OrderByDescending(f => f.SpecialFolderType == SpecialFolderType.Draft || f.RemoteFolderId == ServiceConstants.DRAFT_LABEL_ID)
                .ToList();

            var totalFolders = syncableFolders.Count;
            var totalMessagesDownloaded = 0;

            for (int i = 0; i < totalFolders; i++)
            {
                var folder = syncableFolders[i];
                cancellationToken.ThrowIfCancellationRequested();

                UpdateSyncProgress(totalFolders, totalFolders - (i + 1), $"Syncing {folder.FolderName}...");

                _logger.Debug("Downloading messages for folder {FolderName} (label: {LabelId})", folder.FolderName, folder.RemoteFolderId);

                var folderDownloaded = 0;
                string pageToken = null;

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var request = _gmailService.Users.Messages.List("me");
                    request.LabelIds = new global::Google.Apis.Util.Repeatable<string>(new[] { folder.RemoteFolderId });
                    request.IncludeSpamTrash = true;
                    request.MaxResults = 500; // API max is 500
                    request.PageToken = pageToken;
                    request.Q = queryText;

                    var response = await request.ExecuteAsync(cancellationToken);

                    if (response.Messages != null && response.Messages.Count > 0)
                    {
                        // Filter out already downloaded messages to avoid duplicates
                        var newMessageIds = response.Messages
                            .Select(m => m.Id)
                            .Where(id => !downloadedMessageIds.Contains(id))
                            .ToList();

                        if (newMessageIds.Count > 0)
                        {
                            // Draft folder needs MIME during initial sync so compose can open immediately.
                            bool shouldDownloadRawMime = folder.SpecialFolderType == SpecialFolderType.Draft || folder.RemoteFolderId == ServiceConstants.DRAFT_LABEL_ID;
                            await DownloadMessagesInBatchAsync(
                                newMessageIds,
                                downloadRawMime: shouldDownloadRawMime,
                                cancellationToken: cancellationToken).ConfigureAwait(false);

                            foreach (var id in newMessageIds)
                            {
                                downloadedMessageIds.Add(id);
                            }

                            folderDownloaded += newMessageIds.Count;
                            totalMessagesDownloaded += newMessageIds.Count;
                        }

                        _logger.Debug("Folder {FolderName}: Downloaded {New} new messages ({Total} total in folder)",
                            folder.FolderName, newMessageIds.Count, folderDownloaded);
                    }

                    pageToken = response.NextPageToken;

                } while (!string.IsNullOrEmpty(pageToken));

                _logger.Information("Folder {FolderName}: Downloaded {Count} messages", folder.FolderName, folderDownloaded);
                UpdateSyncProgress(totalFolders, 0, Translator.SyncAction_SynchronizingAccount);
            }

            _logger.Information("Initial sync completed. Downloaded {Count} unique messages for {Name}", downloadedMessageIds.Count, Account.Name);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.Warning("Rate limit exceeded during initial sync. Retrying after delay.");
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during initial sync for {Name}", Account.Name);
            throw;
        }

        return downloadedMessageIds.ToList();
    }

    /// <summary>
    /// Performs incremental synchronization using Gmail History API.
    /// This is the recommended approach for Gmail sync after initial sync is complete.
    /// Returns a result indicating downloaded messages and whether a full resync is needed.
    /// </summary>
    private async Task<DeltaSyncResult> SynchronizeDeltaAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();

        try
        {
            string pageToken = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var historyRequest = _gmailService.Users.History.List("me");
                historyRequest.StartHistoryId = ulong.Parse(Account.SynchronizationDeltaIdentifier!);

                if (!string.IsNullOrEmpty(pageToken))
                    historyRequest.PageToken = pageToken;

                var historyResponse = await historyRequest.ExecuteAsync(cancellationToken);

                if (historyResponse.History != null)
                {
                    var addedMessageIds = new List<string>();

                    // Collect all added messages first
                    foreach (var historyRecord in historyResponse.History)
                    {
                        if (historyRecord.MessagesAdded != null)
                        {
                            addedMessageIds.AddRange(historyRecord.MessagesAdded.Select(ma => ma.Message.Id));
                        }
                    }

                    // Process added messages in batches if any
                    // During delta sync, download with Raw format to get MIME content for new messages
                    if (addedMessageIds.Count != 0)
                    {
                        // Deduplicate message IDs
                        var uniqueAddedIds = addedMessageIds.Distinct().ToList();
                        await DownloadMessagesInBatchAsync(
                            uniqueAddedIds,
                            downloadRawMime: true,
                            suppressMatchingLocalFilters: true,
                            cancellationToken).ConfigureAwait(false);
                        downloadedMessageIds.AddRange(uniqueAddedIds);
                    }

                    // Process other history changes (label changes, deletions)
                    await ProcessHistoryChangesAsync(historyResponse).ConfigureAwait(false);
                }

                // CRITICAL: Update the history ID to the latest one after processing all changes
                // History IDs are always incremental, so the response contains the latest history ID
                if (historyResponse.HistoryId.HasValue)
                {
                    await UpdateAccountSyncIdentifierAsync(historyResponse.HistoryId.Value).ConfigureAwait(false);
                    _logger.Debug("Updated history ID to {HistoryId} after delta sync", historyResponse.HistoryId.Value);
                }

                pageToken = historyResponse.NextPageToken;

            } while (!string.IsNullOrEmpty(pageToken));

            _logger.Information("Delta sync completed. Downloaded {Count} new messages for {Name}", downloadedMessageIds.Count, Account.Name);

            return new DeltaSyncResult(downloadedMessageIds, RequiresFullResync: false);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound ||
                                            (int)ex.HttpStatusCode == 410) // Gone - history expired
        {
            // History ID is no longer valid (expired or not found)
            // This happens when:
            // 1. The history ID is too old (Gmail keeps history for ~30 days)
            // 2. The account was reset or history was cleared
            // Reset the sync identifier and signal that a full resync is needed
            _logger.Warning("History ID {HistoryId} expired or not found for {Name}. Full resync required. Error: {Error}",
                Account.SynchronizationDeltaIdentifier, Account.Name, ex.Message);

            // Clear the sync identifier to trigger initial sync
            Account.SynchronizationDeltaIdentifier = await _gmailChangeProcessor
                .UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, null)
                .ConfigureAwait(false);

            return new DeltaSyncResult(downloadedMessageIds, RequiresFullResync: true);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.Warning("Rate limit exceeded during delta sync for {Name}. Retrying after delay.", Account.Name);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw;
        }
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        _logger.Information("Internal calendar synchronization started for {Name}", Account.Name);

        cancellationToken.ThrowIfCancellationRequested();

        await SynchronizeCalendarsAsync(cancellationToken).ConfigureAwait(false);

        if (options?.Type == CalendarSynchronizationType.CalendarMetadata)
            return CalendarSynchronizationResult.Empty;

        bool isInitialSync = string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier);

        _logger.Debug("Is initial synchronization: {IsInitialSync}", isInitialSync);

        var localCalendars = (await _gmailChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false))
            .Where(c => c.IsSynchronizationEnabled)
            .ToList();

        var totalCalendars = localCalendars.Count;
        if (totalCalendars > 0)
        {
            UpdateSyncProgress(totalCalendars, totalCalendars, Translator.SyncAction_SynchronizingCalendarEvents);
        }

        for (int i = 0; i < totalCalendars; i++)
        {
            var calendar = localCalendars[i];

            try
            {
                var allEvents = await DownloadCalendarEventsAsync(calendar, cancellationToken).ConfigureAwait(false);

                var eventByRemoteId = allEvents
                    .Where(e => !string.IsNullOrWhiteSpace(e.Id))
                    .GroupBy(e => e.Id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                foreach (var @event in OrderCalendarEventsForPersistence(allEvents))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await EnsureRecurringParentProcessedAsync(calendar, @event, eventByRemoteId, cancellationToken).ConfigureAwait(false);
                        await _gmailChangeProcessor.ManageCalendarEventAsync(@event, calendar, Account).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var errorContext = new SynchronizerErrorContext
                        {
                            Account = Account,
                            ErrorMessage = ex.Message,
                            Exception = ex,
                            CalendarId = calendar.Id,
                            CalendarName = calendar.Name,
                            OperationType = "CalendarEventSync",
                            Severity = SynchronizerErrorSeverity.Recoverable
                        };

                        _ = await _gmailSynchronizerErrorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                        CaptureSynchronizationIssue(errorContext);
                        _logger.Error(ex, "Failed to process Gmail event {EventId} for calendar {CalendarName}", @event.Id, calendar.Name);
                    }
                }

                await _gmailChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
                UpdateSyncProgress(totalCalendars, totalCalendars - (i + 1), Translator.SyncAction_SynchronizingCalendarEvents);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var errorContext = new SynchronizerErrorContext
                {
                    Account = Account,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    CalendarId = calendar.Id,
                    CalendarName = calendar.Name,
                    OperationType = "CalendarSync"
                };

                _ = await _gmailSynchronizerErrorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);

                if (!errorContext.CanContinueSync)
                    throw;

                UpdateSyncProgress(totalCalendars, totalCalendars - (i + 1), Translator.SyncAction_SynchronizingCalendarEvents);
            }
        }

        return CalendarSynchronizationResult.Empty;
    }

    private async Task<List<Event>> DownloadCalendarEventsAsync(
        AccountCalendar calendar,
        CancellationToken cancellationToken)
    {
        var currentSyncToken = calendar.SynchronizationDeltaToken;

        try
        {
            return await DownloadCalendarEventsAsync(calendar, currentSyncToken, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (
            !string.IsNullOrWhiteSpace(currentSyncToken) &&
            ex.HttpStatusCode == System.Net.HttpStatusCode.Gone)
        {
            // Google invalidates calendar sync tokens independently from OAuth access tokens.
            // Persist the reset before retrying so a cancelled/failed full sync cannot leave the
            // account permanently retrying the rejected token.
            _logger.Warning(
                ex,
                "Calendar sync token expired for {CalendarName} in {Name}. Retrying with a full sync.",
                calendar.Name,
                Account.Name);

            calendar.SynchronizationDeltaToken = null;
            await _gmailChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);

            return await DownloadCalendarEventsAsync(calendar, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<Event>> DownloadCalendarEventsAsync(
        AccountCalendar calendar,
        string syncToken,
        CancellationToken cancellationToken)
    {
        var request = _calendarService.Events.List(calendar.RemoteCalendarId);

        // Fetch individual event instances (including recurring event occurrences)
        // rather than recurring event masters. This ensures we get all occurrences
        // as separate events that can be stored and displayed directly.
        request.SingleEvents = true;
        request.ShowDeleted = true;

        if (!string.IsNullOrWhiteSpace(syncToken))
        {
            request.SyncToken = syncToken;
        }
        else
        {
            request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow.AddYears(-1);
        }

        string nextPageToken;
        string nextSyncToken = null;
        var allEvents = new List<Event>();

        do
        {
            var events = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            if (events.Items != null)
            {
                allEvents.AddRange(events.Items);
            }

            nextPageToken = events.NextPageToken;
            nextSyncToken = events.NextSyncToken;
            request.PageToken = nextPageToken;
        }
        while (!string.IsNullOrEmpty(nextPageToken));

        calendar.SynchronizationDeltaToken = nextSyncToken;
        return allEvents;
    }

    private static IEnumerable<Event> OrderCalendarEventsForPersistence(IEnumerable<Event> events)
        => events
            .OrderBy(e => !string.IsNullOrWhiteSpace(e.RecurringEventId))
            .ThenByDescending(e => !string.IsNullOrWhiteSpace(GoogleIntegratorExtensions.GetRecurrenceString(e)))
            .ThenBy(e => GoogleIntegratorExtensions.GetEventDateTimeOffset(e.Start) ?? DateTimeOffset.MinValue);

    private async Task EnsureRecurringParentProcessedAsync(
        AccountCalendar calendar,
        Event calendarEvent,
        Dictionary<string, Event> eventByRemoteId,
        CancellationToken cancellationToken)
    {
        var recurringEventId = calendarEvent?.RecurringEventId;
        if (string.IsNullOrWhiteSpace(recurringEventId))
            return;

        var parentItem = await _gmailChangeProcessor.GetCalendarItemAsync(calendar.Id, recurringEventId).ConfigureAwait(false);
        if (parentItem != null)
            return;

        if (!eventByRemoteId.TryGetValue(recurringEventId, out var parentEvent))
        {
            try
            {
                parentEvent = await _calendarService.Events.Get(calendar.RemoteCalendarId, recurringEventId)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                _logger.Warning(ex,
                    "Failed to fetch recurring parent {ParentRemoteEventId} for child {ChildRemoteEventId} in calendar {CalendarName}",
                    recurringEventId,
                    calendarEvent.Id,
                    calendar.Name);
            }

            if (parentEvent != null && !string.IsNullOrWhiteSpace(parentEvent.Id))
            {
                eventByRemoteId[parentEvent.Id] = parentEvent;
            }
        }

        if (parentEvent == null)
        {
            _logger.Warning(
                "Recurring parent {ParentRemoteEventId} is still missing for child {ChildRemoteEventId} in calendar {CalendarName}",
                recurringEventId,
                calendarEvent.Id,
                calendar.Name);
            return;
        }

        await _gmailChangeProcessor.ManageCalendarEventAsync(parentEvent, calendar, Account).ConfigureAwait(false);
    }

    private async Task SynchronizeCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var calendarListRequest = _calendarService.CalendarList.List();
        var calendarListResponse = await calendarListRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (calendarListResponse.Items == null)
        {
            _logger.Warning("No calendars found for {Name}", Account.Name);
            return;
        }

        var localCalendars = await _gmailChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        var remotePrimaryCalendarId = GetPrimaryCalendarId(calendarListResponse.Items);
        var usedCalendarColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<AccountCalendar> insertedCalendars = new();
        List<AccountCalendar> updatedCalendars = new();
        List<AccountCalendar> deletedCalendars = new();

        // 1. Handle deleted calendars.

        foreach (var calendar in localCalendars)
        {
            var remoteCalendar = calendarListResponse.Items.FirstOrDefault(a => a.Id == calendar.RemoteCalendarId);
            if (remoteCalendar == null)
            {
                // Local calendar doesn't exists remotely. Delete local copy.

                await _gmailChangeProcessor.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
                deletedCalendars.Add(calendar);
            }
        }

        // Delete the deleted folders from local list.
        deletedCalendars.ForEach(a => localCalendars.Remove(a));

        // 2. Handle update/insert based on remote calendars.
        foreach (var calendar in calendarListResponse.Items)
        {
            var existingLocalCalendar = localCalendars.FirstOrDefault(a => a.RemoteCalendarId == calendar.Id);
            if (existingLocalCalendar == null)
            {
                // Insert new calendar.
                var remoteBackgroundColor = GetRemoteGmailCalendarBackgroundColor(calendar);
                var fallbackColor = ColorHelpers.GetDistinctFlatColorHex(usedCalendarColors, remoteBackgroundColor);
                var localCalendar = calendar.AsCalendar(Account.Id, fallbackColor);
                localCalendar.IsPrimary = string.Equals(localCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
                localCalendar.BackgroundColorHex = ResolveSynchronizedCalendarBackgroundColor(remoteBackgroundColor, localCalendar, usedCalendarColors);
                localCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(localCalendar.BackgroundColorHex);
                usedCalendarColors.Add(localCalendar.BackgroundColorHex);
                insertedCalendars.Add(localCalendar);
            }
            else
            {
                // Update existing calendar. Right now we only update the name.
                var resolvedColor = ResolveSynchronizedCalendarBackgroundColor(GetRemoteGmailCalendarBackgroundColor(calendar), existingLocalCalendar, usedCalendarColors);
                if (ShouldUpdateCalendar(calendar, existingLocalCalendar, remotePrimaryCalendarId) ||
                    !string.Equals(existingLocalCalendar.BackgroundColorHex, resolvedColor, StringComparison.OrdinalIgnoreCase))
                {
                    existingLocalCalendar.Name = calendar.Summary;
                    existingLocalCalendar.TimeZone = calendar.TimeZone;
                    existingLocalCalendar.BackgroundColorHex = resolvedColor;
                    existingLocalCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(existingLocalCalendar.BackgroundColorHex);
                    existingLocalCalendar.IsPrimary = string.Equals(existingLocalCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
                    existingLocalCalendar.IsReadOnly = !string.Equals(calendar.AccessRole, "owner", StringComparison.OrdinalIgnoreCase)
                                                      && !string.Equals(calendar.AccessRole, "writer", StringComparison.OrdinalIgnoreCase);

                    updatedCalendars.Add(existingLocalCalendar);
                }
                else
                {
                    // Remove it from the local folder list to skip additional calendar updates.
                    localCalendars.Remove(existingLocalCalendar);
                }

                usedCalendarColors.Add(resolvedColor);
            }
        }

        // 3.Process changes in order-> Insert, Update. Deleted ones are already processed.
        foreach (var calendar in insertedCalendars)
        {
            await _gmailChangeProcessor.InsertAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        foreach (var calendar in updatedCalendars)
        {
            await _gmailChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        if (insertedCalendars.Any() || deletedCalendars.Any() || updatedCalendars.Any())
        {
            // TODO: Notify calendar updates.
            // WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
        }
    }

    private async Task InitializeArchiveFolderAsync()
    {
        var localFolders = await _gmailChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);

        // Handling of Gmail special virtual Archive folder.
        // We will generate a new virtual folder if doesn't exist.

        if (!localFolders.Any(a => a.SpecialFolderType == SpecialFolderType.Archive && a.RemoteFolderId == ServiceConstants.ARCHIVE_LABEL_ID))
        {
            archiveFolderId = Guid.NewGuid();

            var archiveFolder = new MailItemFolder()
            {
                FolderName = "Archive", // will be localized. N/A
                RemoteFolderId = ServiceConstants.ARCHIVE_LABEL_ID,
                Id = archiveFolderId.Value,
                MailAccountId = Account.Id,
                SpecialFolderType = SpecialFolderType.Archive,
                IsSynchronizationEnabled = true,
                IsSystemFolder = true,
                IsSticky = true,
                IsHidden = false,
                ShowUnreadCount = true
            };

            await _gmailChangeProcessor.InsertFolderAsync(archiveFolder).ConfigureAwait(false);
            _isFolderStructureChanged = true;

            // Migration-> User might've already have another special folder for Archive.
            // We must remove that type assignment.
            // This code can be removed after sometime.

            var otherArchiveFolders = localFolders.Where(a => a.SpecialFolderType == SpecialFolderType.Archive && a.Id != archiveFolderId.Value).ToList();

            if (otherArchiveFolders.Any())
            {
                _isFolderStructureChanged = true;
            }

            foreach (var otherArchiveFolder in otherArchiveFolders)
            {
                otherArchiveFolder.SpecialFolderType = SpecialFolderType.Other;
                await _gmailChangeProcessor.UpdateFolderAsync(otherArchiveFolder).ConfigureAwait(false);
            }
        }
        else
        {
            archiveFolderId = localFolders.First(a => a.SpecialFolderType == SpecialFolderType.Archive && a.RemoteFolderId == ServiceConstants.ARCHIVE_LABEL_ID).Id;
        }
    }

    private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken = default)
    {
        var localFolders = await _gmailChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var folderRequest = _gmailService.Users.Labels.List("me");

        var labelsResponse = await folderRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (labelsResponse.Labels == null)
        {
            _logger.Warning("No folders found for {Name}", Account.Name);
            return;
        }

        List<MailItemFolder> insertedFolders = new();
        List<MailItemFolder> updatedFolders = new();
        List<MailItemFolder> deletedFolders = new();

        // 1. Handle deleted labels.
        foreach (var localFolder in localFolders)
        {
            // Category folder is virtual folder for Wino. Skip it.
            if (localFolder.SpecialFolderType == SpecialFolderType.Category) continue;

            // Gmail's Archive folder is virtual older for Wino. Skip it.
            if (localFolder.SpecialFolderType == SpecialFolderType.Archive) continue;

            var remoteFolder = labelsResponse.Labels.FirstOrDefault(a => a.Id == localFolder.RemoteFolderId);

            if (remoteFolder == null)
            {
                // Local folder doesn't exists remotely. Delete local copy.
                await _gmailChangeProcessor.DeleteFolderAsync(Account.Id, localFolder.RemoteFolderId).ConfigureAwait(false);

                deletedFolders.Add(localFolder);
            }
        }

        // Delete the deleted folders from local list.
        deletedFolders.ForEach(a => localFolders.Remove(a));

        // 2. Handle update/insert based on remote folders.
        foreach (var remoteFolder in labelsResponse.Labels)
        {
            var existingLocalFolder = localFolders.FirstOrDefault(a => a.RemoteFolderId == remoteFolder.Id);

            if (existingLocalFolder == null)
            {
                // Insert new folder.
                var localFolder = remoteFolder.GetLocalFolder(labelsResponse, Account.Id);

                insertedFolders.Add(localFolder);
            }
            else
            {
                // Update existing folder. Right now we only update the name.

                // TODO: Moving folders around different parents. This is not supported right now.
                // We will need more comphrensive folder update mechanism to support this.

                if (ShouldUpdateFolder(remoteFolder, existingLocalFolder))
                {
                    existingLocalFolder.FolderName = GoogleIntegratorExtensions.GetFolderName(remoteFolder.Name);
                    existingLocalFolder.TextColorHex = remoteFolder.Color?.TextColor;
                    existingLocalFolder.BackgroundColorHex = remoteFolder.Color?.BackgroundColor;

                    updatedFolders.Add(existingLocalFolder);
                }
                else
                {
                    // Remove it from the local folder list to skip additional folder updates.
                    localFolders.Remove(existingLocalFolder);
                }
            }
        }

        // 3.Process changes in order-> Insert, Update. Deleted ones are already processed.
        foreach (var folder in insertedFolders)
        {
            await _gmailChangeProcessor.InsertFolderAsync(folder).ConfigureAwait(false);
        }

        foreach (var folder in updatedFolders)
        {
            await _gmailChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
        }

        if (insertedFolders.Any() || deletedFolders.Any() || updatedFolders.Any())
        {
            _isFolderStructureChanged = true;
        }
    }

    private bool ShouldUpdateCalendar(CalendarListEntry calendarListEntry, AccountCalendar accountCalendar, string remotePrimaryCalendarId)
    {
        var remoteCalendarName = calendarListEntry.Summary;
        var remoteTimeZone = calendarListEntry.TimeZone;
        var remoteBackgroundColor = ResolveSynchronizedCalendarBackgroundColor(GetRemoteGmailCalendarBackgroundColor(calendarListEntry), accountCalendar);
        var remoteTextColor = ColorHelpers.GetReadableTextColorHex(remoteBackgroundColor);
        var remoteIsPrimary = string.Equals(calendarListEntry.Id, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
        var remoteIsReadOnly = !string.Equals(calendarListEntry.AccessRole, "owner", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(calendarListEntry.AccessRole, "writer", StringComparison.OrdinalIgnoreCase);

        bool isNameChanged = !string.Equals(accountCalendar.Name, remoteCalendarName, StringComparison.OrdinalIgnoreCase);
        bool isTimeZoneChanged = !string.Equals(accountCalendar.TimeZone, remoteTimeZone, StringComparison.OrdinalIgnoreCase);
        bool isBackgroundColorChanged = !string.Equals(accountCalendar.BackgroundColorHex, remoteBackgroundColor, StringComparison.OrdinalIgnoreCase);
        bool isTextColorChanged = !string.Equals(accountCalendar.TextColorHex, remoteTextColor, StringComparison.OrdinalIgnoreCase);
        bool isPrimaryChanged = accountCalendar.IsPrimary != remoteIsPrimary;
        bool isReadOnlyChanged = accountCalendar.IsReadOnly != remoteIsReadOnly;

        return isNameChanged || isTimeZoneChanged || isBackgroundColorChanged || isTextColorChanged || isPrimaryChanged || isReadOnlyChanged;
    }

    private static string GetRemoteGmailCalendarBackgroundColor(CalendarListEntry calendarListEntry)
        => string.IsNullOrWhiteSpace(calendarListEntry?.BackgroundColor) ? null : calendarListEntry.BackgroundColor;

    private static string ResolveSynchronizedCalendarBackgroundColor(
        string remoteBackgroundColor,
        AccountCalendar accountCalendar,
        ISet<string> usedCalendarColors = null)
    {
        if (accountCalendar.IsBackgroundColorUserOverridden)
            return accountCalendar.BackgroundColorHex;

        var preferredColor = string.IsNullOrWhiteSpace(remoteBackgroundColor)
            ? accountCalendar.BackgroundColorHex
            : remoteBackgroundColor;

        return string.IsNullOrWhiteSpace(remoteBackgroundColor) && usedCalendarColors != null
            ? ColorHelpers.GetDistinctFlatColorHex(usedCalendarColors, preferredColor)
            : preferredColor;
    }

    private string GetPrimaryCalendarId(IList<CalendarListEntry> remoteCalendars)
    {
        if (remoteCalendars == null || remoteCalendars.Count == 0)
            return string.Empty;

        var explicitPrimary = remoteCalendars.FirstOrDefault(c => c.Primary.GetValueOrDefault());
        if (explicitPrimary != null)
            return explicitPrimary.Id;

        var byPrimaryKeyword = remoteCalendars.FirstOrDefault(c => string.Equals(c.Id, "primary", StringComparison.OrdinalIgnoreCase));
        if (byPrimaryKeyword != null)
            return byPrimaryKeyword.Id;

        var byAccountAddress = remoteCalendars.FirstOrDefault(c => string.Equals(c.Id, Account.Address, StringComparison.OrdinalIgnoreCase));
        if (byAccountAddress != null)
            return byAccountAddress.Id;

        return remoteCalendars.First().Id;
    }

    private bool ShouldUpdateFolder(Label remoteFolder, MailItemFolder existingLocalFolder)
    {
        var remoteFolderName = GoogleIntegratorExtensions.GetFolderName(remoteFolder.Name);
        var localFolderName = existingLocalFolder.FolderName ?? string.Empty;

        bool isNameChanged = !localFolderName.Equals(remoteFolderName, StringComparison.Ordinal);
        bool isColorChanged = existingLocalFolder.BackgroundColorHex != remoteFolder.Color?.BackgroundColor ||
                existingLocalFolder.TextColorHex != remoteFolder.Color?.TextColor;

        return isNameChanged || isColorChanged;
    }

    /// <summary>
    /// Returns a single get request to retrieve the message with the given id.
    /// Always uses Metadata format to download only headers and labels - NOT raw MIME content.
    /// MIME content is only downloaded when explicitly needed via DownloadMissingMimeMessageAsync.
    /// </summary>
    /// <param name="messageId">Message to download.</param>
    /// <returns>Get request for message with Metadata format.</returns>
    private UsersResource.MessagesResource.GetRequest CreateSingleMessageGet(string messageId)
    {
        var singleRequest = _gmailService.Users.Messages.Get("me", messageId);

        // Always use Metadata format for synchronization - this populates Payload.Headers
        // but does NOT download the raw MIME content, saving significant bandwidth and time
        singleRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;

        return singleRequest;
    }

    /// <summary>
    /// Returns a single get request to retrieve the message with Raw format (includes MIME).
    /// Used during delta sync to download full message content.
    /// </summary>
    /// <param name="messageId">Message to download.</param>
    /// <returns>Get request for message with Raw format.</returns>
    private UsersResource.MessagesResource.GetRequest CreateSingleMessageGetRaw(string messageId)
    {
        var singleRequest = _gmailService.Users.Messages.Get("me", messageId);

        // Use Raw format to get full MIME content
        singleRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

        return singleRequest;
    }

    /// <summary>
    /// Processes the delta changes for the given history changes.
    /// Message downloads are not handled here since it's better to batch them.
    /// </summary>
    /// <param name="listHistoryResponse">List of history changes.</param>
    private async Task ProcessHistoryChangesAsync(ListHistoryResponse listHistoryResponse)
    {
        _logger.Debug("Processing delta change {HistoryId} for {Name}", listHistoryResponse.HistoryId.GetValueOrDefault(), Account.Name);

        var pendingStateUpdates = new List<MailCopyStateUpdate>();
        var pendingAssignmentCreates = new Dictionary<string, MailFolderAssignmentUpdate>(StringComparer.Ordinal);
        var pendingAssignmentDeletes = new Dictionary<string, MailFolderAssignmentUpdate>(StringComparer.Ordinal);
        var deletedMessageIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var history in listHistoryResponse.History)
        {
            // Handle label additions.
            if (history.LabelsAdded is not null)
            {
                foreach (var addedLabel in history.LabelsAdded)
                {
                    await HandleLabelAssignmentAsync(addedLabel, pendingStateUpdates, pendingAssignmentCreates, pendingAssignmentDeletes).ConfigureAwait(false);
                }
            }

            // Handle label removals.
            if (history.LabelsRemoved is not null)
            {
                foreach (var removedLabel in history.LabelsRemoved)
                {
                    await HandleLabelRemovalAsync(removedLabel, pendingStateUpdates, pendingAssignmentCreates, pendingAssignmentDeletes).ConfigureAwait(false);
                }
            }

            // Handle removed messages.
            if (history.MessagesDeleted is not null)
            {
                foreach (var deletedMessage in history.MessagesDeleted)
                {
                    var messageId = deletedMessage.Message.Id;

                    _logger.Debug("Processing message deletion for {MessageId}", messageId);

                    deletedMessageIds.Add(messageId);
                }
            }
        }

        if (pendingStateUpdates.Count > 0)
        {
            await _gmailChangeProcessor.ApplyMailStateUpdatesAsync(pendingStateUpdates).ConfigureAwait(false);
        }

        if (pendingAssignmentCreates.Count > 0)
        {
            await _gmailChangeProcessor.CreateAssignmentsAsync(Account.Id, pendingAssignmentCreates.Values.ToList()).ConfigureAwait(false);
        }

        if (pendingAssignmentDeletes.Count > 0)
        {
            await _gmailChangeProcessor.DeleteAssignmentsAsync(Account.Id, pendingAssignmentDeletes.Values.ToList()).ConfigureAwait(false);
        }

        if (deletedMessageIds.Count > 0)
        {
            await _gmailChangeProcessor.DeleteMailsAsync(Account.Id, deletedMessageIds).ConfigureAwait(false);
        }
    }

    private static string GetAssignmentChangeKey(string messageId, string labelId)
        => $"{messageId}\u001f{labelId}";

    private static void QueueAssignmentChange(
        Dictionary<string, MailFolderAssignmentUpdate> creates,
        Dictionary<string, MailFolderAssignmentUpdate> deletes,
        MailFolderAssignmentUpdate assignment,
        bool shouldCreate)
    {
        if (assignment == null ||
            string.IsNullOrWhiteSpace(assignment.MailCopyId) ||
            string.IsNullOrWhiteSpace(assignment.RemoteFolderId))
        {
            return;
        }

        var key = GetAssignmentChangeKey(assignment.MailCopyId, assignment.RemoteFolderId);

        if (shouldCreate)
        {
            deletes.Remove(key);
            creates[key] = assignment;
        }
        else
        {
            creates.Remove(key);
            deletes[key] = assignment;
        }
    }

    private async Task HandleArchiveAssignmentAsync(
        string archivedMessageId,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentCreates,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentDeletes)
    {
        if (!archiveFolderId.HasValue)
            return;

        // Ignore if the message is already in the archive.
        bool archived = await _gmailChangeProcessor.IsMailExistsInFolderAsync(archivedMessageId, archiveFolderId.Value).ConfigureAwait(false);

        if (archived) return;

        _logger.Debug("Processing archive assignment for message {Id}", archivedMessageId);
        QueueAssignmentChange(
            pendingAssignmentCreates,
            pendingAssignmentDeletes,
            new MailFolderAssignmentUpdate(archivedMessageId, ServiceConstants.ARCHIVE_LABEL_ID),
            shouldCreate: true);
    }

    private async Task HandleUnarchiveAssignmentAsync(
        string unarchivedMessageId,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentCreates,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentDeletes)
    {
        if (!archiveFolderId.HasValue)
            return;

        // Ignore if the message is not in the archive.
        bool archived = await _gmailChangeProcessor.IsMailExistsInFolderAsync(unarchivedMessageId, archiveFolderId.Value).ConfigureAwait(false);
        if (!archived) return;

        _logger.Debug("Processing un-archive assignment for message {Id}", unarchivedMessageId);
        QueueAssignmentChange(
            pendingAssignmentCreates,
            pendingAssignmentDeletes,
            new MailFolderAssignmentUpdate(unarchivedMessageId, ServiceConstants.ARCHIVE_LABEL_ID),
            shouldCreate: false);
    }

    private async Task HandleLabelAssignmentAsync(
        HistoryLabelAdded addedLabel,
        List<MailCopyStateUpdate> pendingStateUpdates,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentCreates,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentDeletes)
    {
        var messageId = addedLabel.Message.Id;

        _logger.Debug("Processing label assignment for message {MessageId}", messageId);

        foreach (var labelId in addedLabel.LabelIds)
        {
            // ARCHIVE is a virtual folder - handle it separately
            if (labelId == ServiceConstants.ARCHIVE_LABEL_ID)
            {
                await HandleArchiveAssignmentAsync(messageId, pendingAssignmentCreates, pendingAssignmentDeletes).ConfigureAwait(false);
                continue;
            }

            // When UNREAD label is added mark the message as un-read.
            if (labelId == ServiceConstants.UNREAD_LABEL_ID)
                pendingStateUpdates.Add(new MailCopyStateUpdate(messageId, IsRead: false));

            // When STARRED label is added mark the message as flagged.
            if (labelId == ServiceConstants.STARRED_LABEL_ID)
                pendingStateUpdates.Add(new MailCopyStateUpdate(messageId, IsFlagged: true));

            QueueAssignmentChange(
                pendingAssignmentCreates,
                pendingAssignmentDeletes,
                new MailFolderAssignmentUpdate(messageId, labelId),
                shouldCreate: true);
        }
    }

    private async Task HandleLabelRemovalAsync(
        HistoryLabelRemoved removedLabel,
        List<MailCopyStateUpdate> pendingStateUpdates,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentCreates,
        Dictionary<string, MailFolderAssignmentUpdate> pendingAssignmentDeletes)
    {
        var messageId = removedLabel.Message.Id;

        _logger.Debug("Processing label removed for message {MessageId}", messageId);

        foreach (var labelId in removedLabel.LabelIds)
        {
            // ARCHIVE is a virtual folder - handle it separately
            if (labelId == ServiceConstants.ARCHIVE_LABEL_ID)
            {
                await HandleUnarchiveAssignmentAsync(messageId, pendingAssignmentCreates, pendingAssignmentDeletes).ConfigureAwait(false);
                continue;
            }

            // When UNREAD label is removed mark the message as read.
            if (labelId == ServiceConstants.UNREAD_LABEL_ID)
                pendingStateUpdates.Add(new MailCopyStateUpdate(messageId, IsRead: true));

            // When STARRED label is removed mark the message as un-flagged.
            if (labelId == ServiceConstants.STARRED_LABEL_ID)
                pendingStateUpdates.Add(new MailCopyStateUpdate(messageId, IsFlagged: false));

            QueueAssignmentChange(
                pendingAssignmentCreates,
                pendingAssignmentDeletes,
                new MailFolderAssignmentUpdate(messageId, labelId),
                shouldCreate: false);
        }
    }

    /// <summary>
    /// Prepares Gmail Draft object from Google SDK.
    /// If provided, ThreadId ties the draft to a thread. Used when replying messages.
    /// If provided, DraftId updates the draft instead of creating a new one.
    /// </summary>
    /// <param name="mimeMessage">MailKit MimeMessage to include as raw message into Gmail request.</param>
    /// <param name="messageThreadId">ThreadId that this draft should be tied to.</param>
    /// <param name="messageDraftId">Existing DraftId from Gmail to update existing draft.</param>
    /// <returns></returns>
    private Draft PrepareGmailDraft(MimeMessage mimeMessage, string messageThreadId = "", string messageDraftId = "")
    {
        mimeMessage.Prepare(EncodingConstraint.None);

        var mimeString = mimeMessage.ToString();
        var base64UrlEncodedMime = Base64UrlEncoder.Encode(mimeString);

        var nativeMessage = new Message()
        {
            Raw = base64UrlEncodedMime,
        };

        if (!string.IsNullOrEmpty(messageThreadId))
            nativeMessage.ThreadId = messageThreadId;

        var draft = new Draft()
        {
            Message = nativeMessage,
            Id = messageDraftId
        };

        return draft;
    }

    #region Mail Integrations

    public override List<IRequestBundle<IGoogleApiRequest>> Move(BatchMoveRequest request)
    {
        var toFolder = request[0].ToFolder;
        var fromFolder = request[0].FromFolder;

        // Sent label can't be removed from mails for Gmail.
        // They are automatically assigned by Gmail.
        // When you delete sent mail from gmail web portal, it's moved to Trash
        // but still has Sent label. It's just hidden from the user.
        // Proper assignments will be done later on CreateAssignment call to mimic this behavior.

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
            AddLabelIds = [toFolder.RemoteFolderId]
        };

        // Archived item is being moved to different folder.
        // Unarchive will move it to Inbox, so this is a different case.
        // We can't remove ARCHIVE label because it's a virtual folder and does not exist in Gmail.
        // We will just add the target label and Gmail will handle the rest.

        if (fromFolder.SpecialFolderType == SpecialFolderType.Archive)
        {
            batchModifyRequest.AddLabelIds = [toFolder.RemoteFolderId];
        }
        else if (fromFolder.SpecialFolderType != SpecialFolderType.Sent)
        {
            // Only add remove label ids if the source folder is not sent folder.
            batchModifyRequest.RemoveLabelIds = [fromFolder.RemoteFolderId];
        }

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> ChangeFlag(BatchChangeFlagRequest request)
    {
        bool isFlagged = request[0].IsFlagged;

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
        };

        if (isFlagged)
            batchModifyRequest.AddLabelIds = new List<string>() { ServiceConstants.STARRED_LABEL_ID };
        else
            batchModifyRequest.RemoveLabelIds = new List<string>() { ServiceConstants.STARRED_LABEL_ID };

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> ChangeJunkState(BatchChangeJunkStateRequest request)
    {
        bool isJunk = request[0].IsJunk;

        var addLabelIds = new HashSet<string>();
        var removeLabelIds = new HashSet<string>();

        if (isJunk)
        {
            addLabelIds.Add(ServiceConstants.SPAM_LABEL_ID);
            removeLabelIds.Add(ServiceConstants.INBOX_LABEL_ID);
        }
        else
        {
            addLabelIds.Add(ServiceConstants.INBOX_LABEL_ID);
            removeLabelIds.Add(ServiceConstants.SPAM_LABEL_ID);
        }

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
            AddLabelIds = addLabelIds.ToList(),
            RemoveLabelIds = removeLabelIds.ToList()
        };

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> MarkRead(BatchMarkReadRequest request)
    {
        bool readStatus = request[0].IsRead;

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
        };

        if (readStatus)
            batchModifyRequest.RemoveLabelIds = new List<string>() { ServiceConstants.UNREAD_LABEL_ID };
        else
            batchModifyRequest.AddLabelIds = new List<string>() { ServiceConstants.UNREAD_LABEL_ID };

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> Delete(BatchDeleteRequest request)
    {
        var batchModifyRequest = new BatchDeleteMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
        };

        var networkCall = _gmailService.Users.Messages.BatchDelete(batchModifyRequest, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> CreateDraft(CreateDraftRequest singleRequest)
    {
        Draft draft = null;

        // It's new mail. Not a reply
        if (singleRequest.DraftPreperationRequest.ReferenceMailCopy == null)
            draft = PrepareGmailDraft(singleRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage);
        else
            draft = PrepareGmailDraft(singleRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage,
                singleRequest.DraftPreperationRequest.ReferenceMailCopy.ThreadId,
                singleRequest.DraftPreperationRequest.ReferenceMailCopy.DraftId);

        var networkCall = _gmailService.Users.Drafts.Create(draft, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, singleRequest, singleRequest)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> Archive(BatchArchiveRequest request)
    {
        bool isArchiving = request[0].IsArchiving;
        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList()
        };

        if (isArchiving)
        {
            batchModifyRequest.RemoveLabelIds = new[] { ServiceConstants.INBOX_LABEL_ID };
        }
        else
        {
            batchModifyRequest.AddLabelIds = new[] { ServiceConstants.INBOX_LABEL_ID };
        }

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> SendDraft(SendDraftRequest singleDraftRequest)
    {

        var message = new Message();

        if (!string.IsNullOrEmpty(singleDraftRequest.Item.ThreadId))
        {
            message.ThreadId = singleDraftRequest.Item.ThreadId;
        }

        // Local draft mapping header must never leak to recipients.
        singleDraftRequest.Request.Mime.Headers.Remove(Domain.Constants.WinoLocalDraftHeader);

        singleDraftRequest.Request.Mime.Prepare(EncodingConstraint.None);

        var mimeString = singleDraftRequest.Request.Mime.ToString();
        var base64UrlEncodedMime = Base64UrlEncoder.Encode(mimeString);
        message.Raw = base64UrlEncodedMime;

        var draft = new Draft()
        {
            Id = singleDraftRequest.Request.MailItem.DraftId,
            Message = message
        };

        var networkCall = _gmailService.Users.Drafts.Send(draft, "me");

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, singleDraftRequest, singleDraftRequest)];
    }

    public override async Task<List<MailCopy>> OnlineSearchAsync(RemoteMailSearchCriteria criteria, List<IMailItemFolder> folders, CancellationToken cancellationToken = default)
    {
        var queryText = BuildOnlineSearchQuery(criteria);
        if (string.IsNullOrWhiteSpace(queryText))
            return [];

        static bool IsArchiveFolder(IMailItemFolder folder)
            => folder?.SpecialFolderType == SpecialFolderType.Archive || folder?.RemoteFolderId == ServiceConstants.ARCHIVE_LABEL_ID;

        var distinctFolders = folders?
            .Where(folder => folder != null)
            .GroupBy(folder => folder.Id)
            .Select(group => group.First())
            .ToList();

        var messageIds = new HashSet<string>(StringComparer.Ordinal);

        async Task CollectMessageIdsAsync(UsersResource.MessagesResource.ListRequest request)
        {
            string pageToken = null;

            do
            {
                if (!string.IsNullOrEmpty(pageToken))
                {
                    request.PageToken = pageToken;
                }

                var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (response.Messages == null || response.Messages.Count == 0) break;

                foreach (var message in response.Messages)
                {
                    if (!string.IsNullOrEmpty(message.Id))
                    {
                        messageIds.Add(message.Id);
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
        }

        bool hasScopedQuery = queryText.StartsWith("label:", StringComparison.OrdinalIgnoreCase) ||
                              queryText.StartsWith("in:", StringComparison.OrdinalIgnoreCase);

        if (hasScopedQuery || distinctFolders?.Count == 0)
        {
            var request = _gmailService.Users.Messages.List("me");
            request.Q = queryText;
            request.MaxResults = 500;

            await CollectMessageIdsAsync(request).ConfigureAwait(false);
        }
        else
        {
            foreach (var folder in distinctFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = _gmailService.Users.Messages.List("me");
                request.MaxResults = 500;

                if (IsArchiveFolder(folder))
                {
                    // Gmail archive is virtual. Query via search operator instead of label id.
                    request.Q = $"in:archive {queryText}".Trim();
                }
                else
                {
                    request.Q = queryText;
                    request.LabelIds = new List<string> { folder.RemoteFolderId };
                }

                await CollectMessageIdsAsync(request).ConfigureAwait(false);
            }
        }

        if (messageIds.Count == 0)
            return [];

        var messageIdList = messageIds.ToList();

        // Do not download messages that already exist locally.
        var existingMessageIds = await _gmailChangeProcessor.AreMailsExistsAsync(messageIdList).ConfigureAwait(false);
        var messagesToDownload = messageIdList.Except(existingMessageIds, StringComparer.Ordinal);

        // Download missing messages in batch with metadata only.
        await DownloadMessagesInBatchAsync(messagesToDownload, cancellationToken).ConfigureAwait(false);

        // Get results from database and return.
        return await _gmailChangeProcessor.GetMailCopiesAsync(messageIdList).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads multiple messages in batches with metadata only (no MIME) and creates mail packages.
    /// Uses Gmail batch API to download up to MaximumAllowedBatchRequestSize messages per request.
    /// Used for initial sync where MIME is not needed.
    /// </summary>
    /// <param name="messageIds">List of Gmail message IDs to download</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task DownloadMessagesInBatchAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
    {
        await DownloadMessagesInBatchAsync(
            messageIds,
            downloadRawMime: false,
            suppressMatchingLocalFilters: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads multiple messages in batches with optional MIME content and creates mail packages.
    /// Uses Gmail batch API to download up to MaximumAllowedBatchRequestSize messages per request.
    /// </summary>
    /// <param name="messageIds">List of Gmail message IDs to download</param>
    /// <param name="downloadRawMime">True to download Raw format with MIME, false for Metadata only</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task DownloadMessagesInBatchAsync(
        IEnumerable<string> messageIds,
        bool downloadRawMime,
        bool suppressMatchingLocalFilters = false,
        CancellationToken cancellationToken = default)
    {
        var messageIdList = messageIds.ToList();
        if (messageIdList.Count == 0) return;

        // Split into batches based on MaximumAllowedBatchRequestSize
        var batches = messageIdList.Batch((int)MaximumAllowedBatchRequestSize);

        foreach (var batch in batches)
        {
            var batchRequest = new GoogleBatchRequest(_gmailService);
            var downloadedMessages = new List<Message>();
            var batchTasks = new List<Task>();

            foreach (var messageId in batch)
            {
                var request = downloadRawMime ? CreateSingleMessageGetRaw(messageId) : CreateSingleMessageGet(messageId);

                batchRequest.Queue<Message>(request, (message, error, index, httpMessage) =>
                {
                    var task = Task.Run(async () =>
                    {
                        if (error != null)
                        {
                            _logger.Warning("Failed to download message {MessageId}: {Error}", messageId, error.Message);
                            return;
                        }

                        if (message != null)
                        {
                            lock (downloadedMessages)
                            {
                                downloadedMessages.Add(message);
                            }
                        }
                    });

                    batchTasks.Add(task);
                });
            }

            // Execute the batch request
            await batchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(batchTasks).ConfigureAwait(false);

            // Process all downloaded messages
            var pendingPackages = new List<NewMailItemPackage>();

            foreach (var gmailMessage in downloadedMessages)
            {
                try
                {
                    // Create mail packages from metadata/raw.
                    // If Gmail response is Raw format, CreateNewMailPackagesAsync will parse MIME and
                    // include it in package(s) so it can be saved to disk.
                    var packages = await CreateNewMailPackagesAsync(gmailMessage, null, cancellationToken).ConfigureAwait(false);

                    if (packages != null)
                    {
                        var shouldSuppressUiChange = false;
                        if (suppressMatchingLocalFilters && _mailFilterExecutor != null)
                        {
                            foreach (var package in packages)
                            {
                                if (await _mailFilterExecutor
                                    .ShouldSuppressNewMessageAsync(
                                        Account.Id,
                                        package.AssignedRemoteFolderId,
                                        package.Copy,
                                        cancellationToken)
                                    .ConfigureAwait(false))
                                {
                                    shouldSuppressUiChange = true;
                                    break;
                                }
                            }
                        }

                        foreach (var package in packages)
                        {
                            pendingPackages.Add(shouldSuppressUiChange
                                ? package with { SuppressUiChange = true }
                                : package);
                        }
                    }

                    // Update sync identifier if available
                    if (gmailMessage.HistoryId.HasValue)
                    {
                        await UpdateAccountSyncIdentifierAsync(gmailMessage.HistoryId.Value).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to process downloaded message {MessageId}", gmailMessage.Id);
                }
            }

            if (pendingPackages.Count > 0)
            {
                await _gmailChangeProcessor.CreateMailsAsync(Account.Id, pendingPackages).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Downloads a single message by ID with metadata only (no MIME) and creates mail packages.
    /// </summary>
    /// <param name="messageId">Gmail message ID to download</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task DownloadSingleMessageMetadataAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var request = CreateSingleMessageGet(messageId);
        var gmailMessage = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (gmailMessage == null)
        {
            _logger.Warning("Failed to download message metadata for {MessageId}", messageId);
            return;
        }

        // Create mail packages from metadata
        var packages = await CreateNewMailPackagesAsync(gmailMessage, null, cancellationToken).ConfigureAwait(false);

        if (packages != null && packages.Count > 0)
        {
            await _gmailChangeProcessor.CreateMailsAsync(Account.Id, packages).ConfigureAwait(false);
        }

        // Update sync identifier if available
        if (gmailMessage.HistoryId.HasValue)
        {
            await UpdateAccountSyncIdentifierAsync(gmailMessage.HistoryId.Value).ConfigureAwait(false);
        }
    }

    public async Task<SemanticMailContent> GetSemanticBodyAsync(
        MailBodyLocator locator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var providerMessageId = locator.ProviderMessageId ?? locator.RemoteMessageId;
        var request = _gmailService.Users.Messages.Get("me", providerMessageId);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        request.Fields = "payload(headers,mimeType,filename,body(data,attachmentId),parts(headers,mimeType,filename,body(data,attachmentId),parts(headers,mimeType,filename,body(data,attachmentId),parts(headers,mimeType,filename,body(data,attachmentId),parts)))))";

        var message = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var parts = new List<GmailMessagePart>();
        CollectSemanticTextParts(message?.Payload, parts);
        var selected = parts.FirstOrDefault(part => string.Equals(part.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
            ?? parts.FirstOrDefault(part => string.Equals(part.MimeType, "text/html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Gmail message contains no indexable text body part.");

        var data = selected.Body?.Data;
        if (string.IsNullOrWhiteSpace(data) && !string.IsNullOrWhiteSpace(selected.Body?.AttachmentId))
        {
            var body = await _gmailService.Users.Messages.Attachments
                .Get("me", providerMessageId, selected.Body.AttachmentId)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            data = body?.Data;
        }

        if (string.IsNullOrWhiteSpace(data))
        {
            throw new InvalidOperationException("Gmail text body part contains no data.");
        }

        var format = string.Equals(selected.MimeType, "text/html", StringComparison.OrdinalIgnoreCase)
            ? MailBodyFormat.Html
            : MailBodyFormat.PlainText;
        return new SemanticMailContent(
            new MailBodyContent(format, Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(data))),
            ParseSemanticFrom(message?.Payload),
            ParseSemanticRecipients(message?.Payload, "To"),
            ParseSemanticRecipients(message?.Payload, "Cc"));
    }

    internal static string BuildOnlineSearchQuery(RemoteMailSearchCriteria criteria)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(criteria.Query)) terms.Add(criteria.Query.Trim());
        if (!string.IsNullOrWhiteSpace(criteria.Sender)) terms.Add($"from:({criteria.Sender.Trim()})");
        if (criteria.ReceivedAfterUtc is { } after) terms.Add($"after:{after.UtcDateTime:yyyy/MM/dd}");
        if (criteria.ReceivedBeforeUtc is { } before) terms.Add($"before:{before.UtcDateTime:yyyy/MM/dd}");
        if (criteria.HasAttachments) terms.Add("has:attachment");
        if (criteria.IsUnread) terms.Add("is:unread");
        if (criteria.IsFlagged) terms.Add("is:starred");
        return string.Join(' ', terms);
    }

    private static IReadOnlyList<global::Wino.Mail.AI.Abstractions.MailAddress> ParseSemanticFrom(GmailMessagePart part)
    {
        var value = part?.Headers?.FirstOrDefault(x => string.Equals(x.Name, "From", StringComparison.OrdinalIgnoreCase))?.Value;
        return !string.IsNullOrWhiteSpace(value) && InternetAddressList.TryParse(value, out var addresses)
            ? addresses.Mailboxes.Select(x => new global::Wino.Mail.AI.Abstractions.MailAddress(x.Address, x.Name)).ToArray()
            : [];
    }

    private static IReadOnlyList<string> ParseSemanticRecipients(GmailMessagePart part, string headerName)
    {
        var value = part?.Headers?.FirstOrDefault(x => string.Equals(x.Name, headerName, StringComparison.OrdinalIgnoreCase))?.Value;
        return !string.IsNullOrWhiteSpace(value) && InternetAddressList.TryParse(value, out var addresses)
            ? addresses.Mailboxes.Select(x => x.Address).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
            : [];
    }

    private static void CollectSemanticTextParts(GmailMessagePart part, ICollection<GmailMessagePart> parts)
    {
        if (part is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(part.Filename) &&
            (string.Equals(part.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(part.MimeType, "text/html", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(part);
        }

        if (part.Parts is null)
        {
            return;
        }

        foreach (var child in part.Parts)
        {
            CollectSemanticTextParts(child, parts);
        }
    }

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem,
                                                           ITransferProgress transferProgress = null,
                                                           CancellationToken cancellationToken = default)
    {
        try
        {
            var request = _gmailService.Users.Messages.Get("me", mailItem.Id);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

            var gmailMessage = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var mimeMessage = gmailMessage.GetGmailMimeMessage();

            if (mimeMessage == null)
            {
                _logger.Warning("Tried to download Gmail Raw Mime with {Id} id and server responded without a data.", mailItem.Id);
                return;
            }

            await _gmailChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.Warning("Gmail message {MailId} not found (404) during MIME download. Deleting locally.", mailItem.Id);
            await _gmailChangeProcessor.DeleteMailAsync(Account.Id, mailItem.Id).ConfigureAwait(false);
            throw new SynchronizerEntityNotFoundException(ex.Message);
        }
    }

    public override async Task DownloadCalendarAttachmentAsync(
        Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
        Wino.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Gmail calendar attachments are stored in Google Drive
            // RemoteAttachmentId contains either FileId or FileUrl
            // For simplicity, we'll try to download from the FileId/FileUrl

            if (string.IsNullOrEmpty(attachment.RemoteAttachmentId))
            {
                _logger.Error("RemoteAttachmentId is empty for attachment {AttachmentId}", attachment.Id);
                throw new InvalidOperationException("RemoteAttachmentId is required to download Gmail calendar attachment.");
            }

            // Gmail calendar attachments are links to Google Drive files
            // The attachment.RemoteAttachmentId is either a FileId or FileUrl
            // Since we can't directly download from Calendar API, this would require Drive API access
            // For now, throw NotSupportedException as Gmail attachments require additional Drive API setup

            _logger.Warning("Gmail calendar attachment download requires Google Drive API access. FileId/URL: {RemoteId}", attachment.RemoteAttachmentId);
            throw new NotSupportedException("Gmail calendar attachments are stored in Google Drive and require additional API configuration to download.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error downloading Gmail calendar attachment {AttachmentId}", attachment.Id);
            throw;
        }
    }

    public override List<IRequestBundle<IGoogleApiRequest>> RenameFolder(RenameFolderRequest request)
    {
        var label = new Label()
        {
            Name = request.NewFolderName
        };

        var networkCall = _gmailService.Users.Labels.Update(label, "me", request.Folder.RemoteFolderId);

        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> EmptyFolder(EmptyFolderRequest request)
    {
        // Create batch delete request.

        var deleteRequests = request.MailsToDelete.Select(a => new DeleteRequest(a));

        return Delete(new BatchDeleteRequest(deleteRequests));
    }

    public override List<IRequestBundle<IGoogleApiRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    public override List<IRequestBundle<IGoogleApiRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        var networkCall = _gmailService.Users.Labels.Delete("me", request.Folder.RemoteFolderId);
        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var parentLabelName = request.Folder.FolderName;

        try
        {
            var parentLabel = _gmailService.Users.Labels.Get("me", request.Folder.RemoteFolderId).Execute();
            if (!string.IsNullOrWhiteSpace(parentLabel?.Name))
            {
                parentLabelName = parentLabel.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to resolve full parent label name for {FolderId}. Falling back to local folder name.", request.Folder.RemoteFolderId);
        }

        var label = new Label()
        {
            Name = $"{parentLabelName}/{request.NewFolderName}"
        };

        var networkCall = _gmailService.Users.Labels.Create(label, "me");
        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var label = new Label()
        {
            Name = request.NewFolderName
        };

        var networkCall = _gmailService.Users.Labels.Create(label, "me");
        return [new HttpRequestBundle<IGoogleApiRequest>(networkCall, request, request)];
    }

    #endregion

    #region Request Execution

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<IGoogleApiRequest>> batchedRequests,
                                                          CancellationToken cancellationToken = default)
    {
        // First apply all UI changes immediately before any batching.
        // This ensures UI reflects changes right away, regardless of batch processing.
        ApplyOptimisticUiChanges(batchedRequests);

        // Batch requests per Google service instance. Calendar requests must be queued against
        // CalendarService, otherwise Gmail's batch endpoint will reject Calendar REST paths.
        var requestGroups = batchedRequests.GroupBy(bundle => bundle.NativeRequest.Service);

        foreach (var requestGroup in requestGroups)
        {
            var batchedBundles = requestGroup.Batch((int)MaximumAllowedBatchRequestSize);

            foreach (var bundle in batchedBundles)
            {
                var nativeBatchRequest = new GoogleBatchRequest(requestGroup.Key);
                var bundleTasks = new List<Task>();

                foreach (var requestBundle in bundle)
                {
                    // UI changes are already applied above before batching.
                    nativeBatchRequest.Queue<object>(requestBundle.NativeRequest, (content, error, index, message)
                        => bundleTasks.Add(ProcessSingleNativeRequestResponseAsync(requestBundle, error, message, cancellationToken)));
                }

                await nativeBatchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(bundleTasks).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessGmailGoogleRequestErrorAsync(GoogleRequestError error, IRequestBundle<IGoogleApiRequest> bundle)
    {
        if (error == null) return;

        if (bundle?.UIChangeRequest is CreateDraftRequest createDraftRequest)
        {
            await _gmailChangeProcessor
                .MarkDraftSyncFailedAsync(createDraftRequest.Item.UniqueId, error.Message)
                .ConfigureAwait(false);
        }

        var isEntityNotFound = IsKnownGmailEntityNotFoundError(error, bundle);

        // Create error context
        var errorContext = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorCode = error.Code,
            ErrorMessage = error.Message,
            RequestBundle = bundle,
            Request = bundle.Request,
            IsEntityNotFound = isEntityNotFound,
            AdditionalData = new Dictionary<string, object>
            {
                { "Error", error }
            }
        };

        // Try to handle the error with registered handlers
        var handled = await _gmailSynchronizerErrorHandlerFactory.HandleErrorAsync(errorContext);

        if (handled)
        {
            if (ShouldRevertOptimisticMailStateChange(bundle?.UIChangeRequest))
            {
                RequestUiChangeCoordinator.RevertBundle(bundle);
            }

            return;
        }

        // If not handled by any specific handler, apply default error handling
        if (!handled)
        {
            CaptureSynchronizationIssue(errorContext);

            // OutOfMemoryException is a known bug in Gmail SDK.
            if (error.Code == 0)
            {
                RequestUiChangeCoordinator.RevertBundle(bundle);
                throw new OutOfMemoryException(error.Message);
            }

            // Entity not found.
            if (isEntityNotFound)
            {
                RequestUiChangeCoordinator.RevertBundle(bundle);
                throw new SynchronizerEntityNotFoundException(error.Message);
            }

            if (!string.IsNullOrEmpty(error.Message))
            {
                RequestUiChangeCoordinator.RevertBundle(bundle);
                error.Errors?.ForEach(error => _logger.Error("Unknown Gmail SDK error for {Name}\n{Error}", Account.Name, error));

                throw new SynchronizerException(error.Message);
            }
        }
    }

    private static bool IsKnownGmailEntityNotFoundError(
        GoogleRequestError error,
        IRequestBundle<IGoogleApiRequest> bundle)
    {
        if (error?.Code != 404 || bundle?.UIChangeRequest == null)
            return false;

        if (!IsExistingEntityOperation(bundle.UIChangeRequest))
            return false;

        var message = error.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalizedMessage = message.ToLowerInvariant();
        return normalizedMessage.Contains("requested entity")
               || normalizedMessage.Contains("message not found")
               || normalizedMessage.Contains("thread not found")
               || normalizedMessage.Contains("draft not found")
               || normalizedMessage.Contains("label not found")
               || normalizedMessage.Contains("event not found")
               || normalizedMessage.Contains("calendar not found");
    }

    protected override Task MarkDraftSyncFailedAsync(Guid mailUniqueId, string error)
        => _gmailChangeProcessor.MarkDraftSyncFailedAsync(mailUniqueId, error);

    private static bool IsExistingEntityOperation(IUIChangeRequest request)
        => request is BatchDeleteRequest
           || request is BatchMoveRequest
           || request is BatchChangeJunkStateRequest
           || request is BatchChangeFlagRequest
           || request is BatchMarkReadRequest
           || request is BatchArchiveRequest
           || request is DeleteRequest
           || request is MoveRequest
           || request is ChangeJunkStateRequest
           || request is ChangeFlagRequest
           || request is MarkReadRequest
           || request is ArchiveRequest
           || request is RenameFolderRequest
           || request is DeleteFolderRequest
           || request is AcceptEventRequest
           || request is DeclineEventRequest
           || request is OutlookDeclineEventRequest
           || request is TentativeEventRequest
           || request is UpdateCalendarEventRequest
           || request is DeleteCalendarEventRequest;

    private static bool ShouldRevertOptimisticMailStateChange(IUIChangeRequest request)
        => request is BatchMarkReadRequest
        || request is MarkReadRequest
        || request is BatchChangeJunkStateRequest
        || request is ChangeJunkStateRequest
        || request is BatchChangeFlagRequest
        || request is ChangeFlagRequest;

    private bool ShouldUpdateSyncIdentifier(ulong? historyId)
    {
        if (historyId == null) return false;

        var newHistoryId = historyId.Value;
        var currentSynchronizationIdentifier = Account.SynchronizationDeltaIdentifier;

        if (string.IsNullOrWhiteSpace(currentSynchronizationIdentifier))
            return true;

        if (!ulong.TryParse(currentSynchronizationIdentifier, out ulong currentIdentifier))
        {
            _logger.Warning("Current Gmail history ID '{HistoryId}' is invalid for {Name}. Replacing it with {NewHistoryId}.",
                currentSynchronizationIdentifier, Account.Name, newHistoryId);
            return true;
        }

        return newHistoryId > currentIdentifier;
    }

    private async Task UpdateAccountSyncIdentifierAsync(ulong? historyId)
    {
        if (ShouldUpdateSyncIdentifier(historyId))
        {
            Account.SynchronizationDeltaIdentifier = await _gmailChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, historyId.Value.ToString());
        }
    }

    private async Task ProcessSingleNativeRequestResponseAsync(IRequestBundle<IGoogleApiRequest> bundle,
                                                               GoogleRequestError error,
                                                               HttpResponseMessage httpResponseMessage,
                                                               CancellationToken cancellationToken = default)
    {
        if (error != null)
        {
            await ProcessGmailGoogleRequestErrorAsync(error, bundle).ConfigureAwait(false);
            return;
        }

        await PersistSuccessfulMailStateChangesAsync(bundle).ConfigureAwait(false);

        if (bundle is HttpRequestBundle<IGoogleApiRequest, Message> messageBundle)
        {
            var gmailMessage = await messageBundle.DeserializeBundleAsync(httpResponseMessage, GmailSynchronizerJsonContext.Default.Message, cancellationToken).ConfigureAwait(false);

            if (gmailMessage == null) return;

            // Create mail packages from the downloaded message
            var packages = await CreateNewMailPackagesAsync(gmailMessage, null, cancellationToken).ConfigureAwait(false);

            if (packages != null && packages.Count > 0)
            {
                await _gmailChangeProcessor.CreateMailsAsync(Account.Id, packages).ConfigureAwait(false);
            }

            await UpdateAccountSyncIdentifierAsync(gmailMessage.HistoryId).ConfigureAwait(false);
        }
        else if (bundle is HttpRequestBundle<IGoogleApiRequest, Label> folderBundle)
        {
            // TODO: Handle new Gmail Label added or updated.
        }
        else if (bundle is HttpRequestBundle<IGoogleApiRequest, Event> eventBundle && eventBundle.Request is CreateCalendarEventRequest createCalendarEventRequest)
        {
            var createdEvent = await eventBundle.DeserializeBundleAsync(httpResponseMessage, GmailSynchronizerJsonContext.Default.Event, cancellationToken).ConfigureAwait(false);

            if (createdEvent == null || string.IsNullOrWhiteSpace(createdEvent.Id))
                return;

            await _gmailChangeProcessor.PersistCreatedCalendarEventAsync(
                createCalendarEventRequest.PreparedItem,
                createCalendarEventRequest.PreparedEvent.Attendees,
                createCalendarEventRequest.PreparedEvent.Reminders,
                createdEvent.Id).ConfigureAwait(false);

            await UploadCalendarEventAttachmentsAsync(createCalendarEventRequest, createdEvent, cancellationToken).ConfigureAwait(false);
        }
        else if (bundle is HttpRequestBundle<IGoogleApiRequest, Draft> draftBundle && draftBundle.Request is CreateDraftRequest createDraftRequest)
        {
            // New draft mail is created.

            var messageDraft = await draftBundle.DeserializeBundleAsync(httpResponseMessage, GmailSynchronizerJsonContext.Default.Draft, cancellationToken).ConfigureAwait(false);

            if (messageDraft == null) return;

            var localDraftCopy = createDraftRequest.DraftPreperationRequest.CreatedLocalDraftCopy;

            // Here we have DraftId, MessageId and ThreadId.
            // Update the local copy properties and re-synchronize to get the original message and update history.

            // We don't fetch the single message here because it may skip some of the history changes when the
            // fetch updates the historyId. Therefore we need to re-synchronize to get the latest history changes
            // which will have the original message downloaded eventually.

            var isMapped = await _gmailChangeProcessor
                .MapLocalDraftAsync(Account.Id, localDraftCopy.UniqueId, messageDraft.Message.Id, messageDraft.Id, messageDraft.Message.ThreadId)
                .ConfigureAwait(false);

            if (!isMapped)
            {
                // The user discarded the local draft while the create request was in flight.
                // Remove the remote draft immediately so the following folder sync cannot
                // download it as a new item and resurrect it in the Drafts list.
                await _gmailService.Users.Drafts.Delete("me", messageDraft.Id)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var options = new MailSynchronizationOptions()
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.FullFolders
            };

            await SynchronizeMailsInternalAsync(options, cancellationToken);
        }
    }

    private async Task PersistSuccessfulMailStateChangesAsync(IRequestBundle<IGoogleApiRequest> bundle)
    {
        switch (bundle.UIChangeRequest)
        {
            case BatchMarkReadRequest batchMarkReadRequest:
                await _gmailChangeProcessor.ApplyMailStateUpdatesAsync(
                    batchMarkReadRequest.Select(request => new MailCopyStateUpdate(request.Item.Id, IsRead: request.IsRead)))
                    .ConfigureAwait(false);
                break;

            case MarkReadRequest markReadRequest:
                await _gmailChangeProcessor.ApplyMailStateUpdatesAsync(
                    [new MailCopyStateUpdate(markReadRequest.Item.Id, IsRead: markReadRequest.IsRead)])
                    .ConfigureAwait(false);
                break;

            case BatchChangeFlagRequest batchChangeFlagRequest:
                await _gmailChangeProcessor.ApplyMailStateUpdatesAsync(
                    batchChangeFlagRequest.Select(request => new MailCopyStateUpdate(request.Item.Id, IsFlagged: request.IsFlagged)))
                    .ConfigureAwait(false);
                break;

            case ChangeFlagRequest changeFlagRequest:
                await _gmailChangeProcessor.ApplyMailStateUpdatesAsync(
                    [new MailCopyStateUpdate(changeFlagRequest.Item.Id, IsFlagged: changeFlagRequest.IsFlagged)])
                    .ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Gmail Archive is a special folder that is not visible in the Gmail web interface.
    /// We need to handle it separately.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task MapArchivedMailsAsync(DateTime? initialSynchronizationCutoffDateUtc, CancellationToken cancellationToken)
    {
        if (!archiveFolderId.HasValue) return;

        var request = _gmailService.Users.Messages.List("me");
        request.Q = BuildGmailSearchQuery("in:archive", initialSynchronizationCutoffDateUtc);
        request.MaxResults = 500;

        string pageToken = null;

        var archivedMessageIds = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            if (!string.IsNullOrEmpty(pageToken)) request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (response.Messages == null) break;

            foreach (var message in response.Messages)
            {
                if (!string.IsNullOrEmpty(message.Id))
                {
                    archivedMessageIds.Add(message.Id);
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        var result = await _gmailChangeProcessor.GetGmailArchiveComparisonResultAsync(archiveFolderId.Value, archivedMessageIds.ToList()).ConfigureAwait(false);

        var addedArchiveIds = result.Added.Distinct(StringComparer.Ordinal).ToList();
        var removedArchiveIds = result.Removed.Distinct(StringComparer.Ordinal).ToList();

        if (addedArchiveIds.Count > 0)
        {
            // Archive sync can surface messages that were never downloaded before.
            // Download metadata first so assignment creation can succeed.
            var existingBeforeDownload = await _gmailChangeProcessor.AreMailsExistsAsync(addedArchiveIds).ConfigureAwait(false);
            var missingArchiveIds = addedArchiveIds.Except(existingBeforeDownload, StringComparer.Ordinal).ToList();

            if (missingArchiveIds.Count > 0)
            {
                await DownloadMessagesInBatchAsync(missingArchiveIds, cancellationToken).ConfigureAwait(false);
            }

            var existingAfterDownload = await _gmailChangeProcessor.AreMailsExistsAsync(addedArchiveIds).ConfigureAwait(false);
            var pendingArchiveCreates = new Dictionary<string, MailFolderAssignmentUpdate>(StringComparer.Ordinal);
            var pendingArchiveDeletes = new Dictionary<string, MailFolderAssignmentUpdate>(StringComparer.Ordinal);

            foreach (var archiveAddedItem in existingAfterDownload)
            {
                await HandleArchiveAssignmentAsync(archiveAddedItem, pendingArchiveCreates, pendingArchiveDeletes).ConfigureAwait(false);
            }

            if (pendingArchiveCreates.Count > 0)
            {
                await _gmailChangeProcessor.CreateAssignmentsAsync(Account.Id, pendingArchiveCreates.Values.ToList()).ConfigureAwait(false);
            }
        }

        var pendingArchiveRemovals = new Dictionary<string, MailFolderAssignmentUpdate>(StringComparer.Ordinal);
        var pendingArchiveCreateOverrides = new Dictionary<string, MailFolderAssignmentUpdate>(StringComparer.Ordinal);

        foreach (var unAarchivedRemovedItem in removedArchiveIds)
        {
            await HandleUnarchiveAssignmentAsync(unAarchivedRemovedItem, pendingArchiveCreateOverrides, pendingArchiveRemovals).ConfigureAwait(false);
        }

        if (pendingArchiveRemovals.Count > 0)
        {
            await _gmailChangeProcessor.DeleteAssignmentsAsync(Account.Id, pendingArchiveRemovals.Values.ToList()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Maps existing Gmail Draft resources to local mail copies.
    /// This uses indexed search, therefore it's quite fast.
    /// It's safe to execute this after each Draft creation + batch message download.
    /// </summary>
    private async Task MapDraftIdsAsync(CancellationToken cancellationToken = default)
    {
        // Check if account has any draft locally.
        // There is no point to send this query if there are no local drafts.

        bool hasLocalDrafts = await _gmailChangeProcessor.HasAccountAnyDraftAsync(Account.Id).ConfigureAwait(false);

        if (!hasLocalDrafts) return;

        var drafts = await _gmailService.Users.Drafts.List("me").ExecuteAsync(cancellationToken);

        if (drafts.Drafts == null)
        {
            _logger.Information("There are no drafts to map for {Name}", Account.Name);

            return;
        }

        foreach (var draft in drafts.Drafts)
        {
            await _gmailChangeProcessor.MapLocalDraftAsync(draft.Message.Id, draft.Id, draft.Message.ThreadId);
        }
    }

    protected override Task<MailCopy> CreateMinimalMailCopyAsync(Message gmailMessage, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        bool isUnread = gmailMessage.GetIsUnread();
        bool isFocused = gmailMessage.GetIsFocused();
        bool isFlagged = gmailMessage.GetIsFlagged();
        bool isDraft = gmailMessage.GetIsDraft();

        // Try to get the most accurate date from Gmail's InternalDate first, then fallback to Date header
        DateTime creationDate = DateTime.UtcNow;

        if (gmailMessage.InternalDate.HasValue)
        {
            // Gmail's InternalDate is in milliseconds since Unix epoch
            creationDate = DateTimeOffset.FromUnixTimeMilliseconds(gmailMessage.InternalDate.Value).UtcDateTime;
        }
        else
        {
            // Fallback to parsing the Date header
            var dateHeaderValue = gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("Date", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrEmpty(dateHeaderValue) && DateTime.TryParse(dateHeaderValue, out var parsedDate))
            {
                creationDate = parsedDate.ToUniversalTime();
            }
        }

        // Extract From header and parse name/address
        var fromHeaderValue = gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("From", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        var (fromName, fromAddress) = ExtractNameAndEmailFromHeader(fromHeaderValue);

        // Detect calendar invitation by checking Content-Type header (only if calendar access granted)
        var itemType = Account.IsCalendarAccessGranted ? GetMailItemTypeFromHeaders(gmailMessage.Payload?.Headers) : MailItemType.Mail;

        var copy = new MailCopy()
        {
            CreationDate = creationDate,
            Subject = HttpUtility.HtmlDecode(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("Subject", StringComparison.OrdinalIgnoreCase))?.Value ?? ""),
            FromName = HttpUtility.HtmlDecode(fromName),
            FromAddress = fromAddress,
            PreviewText = HttpUtility.HtmlDecode(gmailMessage.Snippet ?? "").Trim(),
            ThreadId = gmailMessage.ThreadId,
            Importance = MailImportance.Normal, // Default importance without MIME parsing
            Id = gmailMessage.Id,
            IsDraft = isDraft,
            HasAttachments = gmailMessage.Payload?.Parts?.Any(p => !string.IsNullOrEmpty(p.Filename)) ?? false,
            IsRead = !isUnread,
            IsReadReceiptRequested = HasReadReceiptRequest(gmailMessage.Payload?.Headers),
            IsFlagged = isFlagged,
            IsFocused = isFocused,
            InReplyTo = MailHeaderExtensions.StripAngleBrackets(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase))?.Value),
            MessageId = MailHeaderExtensions.StripAngleBrackets(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("Message-Id", StringComparison.OrdinalIgnoreCase))?.Value),
            References = MailHeaderExtensions.NormalizeReferences(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("References", StringComparison.OrdinalIgnoreCase))?.Value),
            FileId = Guid.NewGuid(),
            ItemType = itemType
        };

        // Note: DraftId is NOT set here. Gmail's Draft resource ID is separate from ThreadId
        // and can only be obtained from the Drafts API (not Messages API).
        // DraftId is populated by:
        // - MapLocalDraftAsync (for Wino-created drafts, from CreateDraft response)
        // - MapDraftIdsAsync (for all drafts, from Drafts.List API)

        return Task.FromResult(copy);
    }

    /// <summary>
    /// Enriches a MailCopy with fields extracted from a parsed MimeMessage.
    /// This is needed when messages are downloaded with Raw format (delta sync),
    /// because the Gmail API does not populate Payload.Headers in Raw format.
    /// Fields already populated (non-null/non-empty) are NOT overwritten.
    /// </summary>
    private static void EnrichMailCopyFromMime(MailCopy copy, MimeMessage mime)
    {
        if (copy == null || mime == null) return;

        if (string.IsNullOrEmpty(copy.Subject))
            copy.Subject = mime.Subject ?? string.Empty;

        if (string.IsNullOrEmpty(copy.FromName))
        {
            var from = mime.From.Mailboxes.FirstOrDefault();
            if (from != null)
                copy.FromName = from.Name ?? string.Empty;
        }

        if (string.IsNullOrEmpty(copy.FromAddress))
        {
            var from = mime.From.Mailboxes.FirstOrDefault();
            if (from != null)
                copy.FromAddress = from.Address ?? string.Empty;
        }

        if (string.IsNullOrEmpty(copy.MessageId))
            copy.MessageId = MailHeaderExtensions.NormalizeMessageId(mime.Headers[HeaderId.MessageId]);

        if (!copy.IsReadReceiptRequested)
            copy.IsReadReceiptRequested = mime.HasReadReceiptRequest();

        if (string.IsNullOrEmpty(copy.InReplyTo))
            copy.InReplyTo = MailHeaderExtensions.NormalizeMessageId(mime.InReplyTo);

        if (string.IsNullOrEmpty(copy.References) && mime.References?.Count > 0)
            copy.References = MailHeaderExtensions.JoinStoredReferences(mime.References);

        if (!copy.HasAttachments && mime.Attachments.Any())
            copy.HasAttachments = true;

        if (copy.Importance == MailImportance.Normal)
        {
            copy.Importance = mime.Importance switch
            {
                MessageImportance.High => MailImportance.High,
                MessageImportance.Low => MailImportance.Low,
                _ => MailImportance.Normal
            };
        }
    }

    /// <summary>
    /// Determines MailItemType based on Gmail message headers.
    /// Gmail doesn't have EventMessage type like Outlook, but calendar invitations can be detected
    /// by checking Content-Type header for text/calendar or multipart/alternative with text/calendar part.
    /// </summary>
    private static MailItemType GetMailItemTypeFromHeaders(IList<MessagePartHeader> headers)
    {
        if (headers == null) return MailItemType.Mail;

        // Check Content-Type header for text/calendar
        var contentTypeHeader = headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;

        if (!string.IsNullOrEmpty(contentTypeHeader))
        {
            // Check if it's a calendar message (text/calendar or multipart with calendar)
            if (contentTypeHeader.Contains("text/calendar", StringComparison.OrdinalIgnoreCase))
            {
                // Check the METHOD parameter to determine invitation type
                var methodMatch = System.Text.RegularExpressions.Regex.Match(contentTypeHeader, @"method=([^;\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (methodMatch.Success)
                {
                    var method = methodMatch.Groups[1].Value.Trim('"').ToUpperInvariant();

                    return method switch
                    {
                        "REQUEST" => MailItemType.CalendarInvitation,
                        "CANCEL" => MailItemType.CalendarCancellation,
                        "REPLY" => MailItemType.CalendarResponse,
                        _ => MailItemType.Mail
                    };
                }

                // If no method specified, assume it's an invitation
                return MailItemType.CalendarInvitation;
            }
        }

        return MailItemType.Mail;
    }

    /// <summary>
    /// Extracts name and email address from a header value like "Name <email@domain.com>" or "email@domain.com"
    /// </summary>
    private static (string name, string email) ExtractNameAndEmailFromHeader(string headerValue)
    {
        if (string.IsNullOrEmpty(headerValue))
            return ("", "");

        // Try to match "Name <email@domain.com>" format
        var match = System.Text.RegularExpressions.Regex.Match(headerValue, @"^(.+?)\s*<(.+?)>$");
        if (match.Success)
        {
            var name = match.Groups[1].Value.Trim().Trim('"');
            var email = match.Groups[2].Value.Trim();
            return (name, email);
        }

        // If no angle brackets, assume the whole value is the email with no name
        var emailOnly = headerValue.Trim();
        return ("", emailOnly);
    }

    private static bool HasReadReceiptRequest(IList<MessagePartHeader> headers)
        => headers?.Any(h => h.Name.Equals(Domain.Constants.DispositionNotificationToHeader, StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(h.Value)) == true;

    private static bool LooksLikeReadReceipt(IList<MessagePartHeader> headers)
    {
        var contentType = headers?.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        return !string.IsNullOrWhiteSpace(contentType)
               && contentType.Contains("disposition-notification", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AccountContact> ExtractContactsFromGmailMessage(Message message, MimeMessage mimeMessage)
    {
        var contacts = new Dictionary<string, AccountContact>(StringComparer.OrdinalIgnoreCase);

        AddFromHeaders(message?.Payload?.Headers);

        if (mimeMessage != null)
        {
            AddFromInternetAddressList(mimeMessage.From);
            AddFromInternetAddressList(mimeMessage.To);
            AddFromInternetAddressList(mimeMessage.Cc);
            AddFromInternetAddressList(mimeMessage.Bcc);
            AddFromInternetAddressList(mimeMessage.ReplyTo);

            if (mimeMessage.Sender is MailboxAddress senderMailbox)
            {
                AddContact(senderMailbox.Address, senderMailbox.Name);
            }
        }

        return contacts.Values.ToList();

        void AddFromHeaders(IList<MessagePartHeader> headers)
        {
            if (headers == null || headers.Count == 0) return;

            AddFromHeader("From");
            AddFromHeader("Sender");
            AddFromHeader("To");
            AddFromHeader("Cc");
            AddFromHeader("Bcc");
            AddFromHeader("Reply-To");

            void AddFromHeader(string headerName)
            {
                var headerValue = headers
                    .FirstOrDefault(h => h.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                    ?.Value;

                if (string.IsNullOrWhiteSpace(headerValue)) return;

                try
                {
                    var addresses = InternetAddressList.Parse(headerValue);
                    foreach (var mailbox in addresses.Mailboxes)
                    {
                        AddContact(mailbox.Address, mailbox.Name);
                    }
                }
                catch
                {
                    var (name, email) = ExtractNameAndEmailFromHeader(headerValue);
                    AddContact(email, name);
                }
            }
        }

        void AddFromInternetAddressList(InternetAddressList addresses)
        {
            if (addresses == null) return;

            foreach (var mailbox in addresses.Mailboxes)
            {
                AddContact(mailbox.Address, mailbox.Name);
            }
        }

        void AddContact(string address, string name)
        {
            var trimmedAddress = address?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedAddress)) return;

            var displayName = string.IsNullOrWhiteSpace(name) ? trimmedAddress : name.Trim();

            contacts[trimmedAddress] = new AccountContact
            {
                Address = trimmedAddress,
                Name = displayName
            };
        }
    }

    /// <summary>
    /// Creates new mail packages for the given message.
    /// AssignedFolder is null since the LabelId is parsed out of the Message.
    /// If Gmail Message includes Raw payload, MIME is parsed and attached to packages.
    /// </summary>
    /// <param name="message">Gmail message to create package for (must have Metadata format).</param>
    /// <param name="assignedFolder">Null, not used.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>New mail package that change processor can use to insert new mail into database.</returns>
    public override async Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Message message,
                                                                              MailItemFolder assignedFolder,
                                                                              CancellationToken cancellationToken = default)
    {
        var packageList = new List<NewMailItemPackage>();
        MimeMessage mimeMessage = null;

        // Raw format is used in delta sync and does not populate Payload.Headers.
        // Parse MIME from Raw so we can resolve draft mapping header and persist mime content.
        if (!string.IsNullOrEmpty(message?.Raw))
        {
            try
            {
                mimeMessage = message.GetGmailMimeMessage();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to parse MIME from raw Gmail message {MessageId}", message?.Id);
            }
        }

        // Create base MailCopy from metadata only - NO MIME download
        var baseMailCopy = await CreateMinimalMailCopyAsync(message, assignedFolder, cancellationToken);

        // Initial sync metadata flow does not include MIME, but calendar invitations need MIME
        // for date rendering and invitation-to-calendar mapping.
        if (mimeMessage == null &&
            (baseMailCopy?.ItemType == MailItemType.CalendarInvitation || LooksLikeReadReceipt(message?.Payload?.Headers)) &&
            !string.IsNullOrEmpty(message?.Id))
        {
            try
            {
                var rawRequest = _gmailService.Users.Messages.Get("me", message.Id);
                rawRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

                var rawMessage = await rawRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(rawMessage?.Raw))
                {
                    mimeMessage = rawMessage.GetGmailMimeMessage();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to fetch raw MIME for Gmail message {MessageId}", message.Id);
            }
        }

        if (mimeMessage != null)
        {
            // Raw responses don't include metadata headers. Backfill important fields from MIME.
            EnrichMailCopyFromMime(baseMailCopy, mimeMessage);
        }

        await TryMapCalendarInvitationAsync(baseMailCopy, mimeMessage, cancellationToken).ConfigureAwait(false);

        var extractedContacts = ExtractContactsFromGmailMessage(message, mimeMessage);

        // Check for local draft mapping using X-Wino-Draft-Id header.
        // For Metadata format we read from Payload.Headers.
        // For Raw format (Payload is null), we read from parsed MIME headers.
        if (baseMailCopy.IsDraft)
        {
            var draftIdHeader = message.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals(Domain.Constants.WinoLocalDraftHeader, StringComparison.OrdinalIgnoreCase))?.Value
                                ?? mimeMessage?.Headers?.FirstOrDefault(h => h.Field.Equals(Domain.Constants.WinoLocalDraftHeader, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrEmpty(draftIdHeader) && Guid.TryParse(draftIdHeader, out _))
            {
                if (Guid.TryParse(draftIdHeader, out Guid localDraftCopyUniqueId))
                {
                    // This message belongs to existing local draft copy.
                    // Map remote ids to local copy and skip creating duplicate rows.
                    bool isMappingSuccessful = await _gmailChangeProcessor.MapLocalDraftAsync(
                        Account.Id,
                        localDraftCopyUniqueId,
                        baseMailCopy.Id,
                        baseMailCopy.DraftId,
                        baseMailCopy.ThreadId).ConfigureAwait(false);

                    if (isMappingSuccessful)
                    {
                        // Keep local draft MIME in sync with the fetched remote raw MIME if available.
                        if (mimeMessage != null)
                        {
                            var mappedDraftCopies = await _gmailChangeProcessor.GetMailCopiesAsync([baseMailCopy.Id]).ConfigureAwait(false);
                            if (mappedDraftCopies != null)
                            {
                                var savedFileIds = new HashSet<Guid>();
                                foreach (var mappedCopy in mappedDraftCopies)
                                {
                                    if (mappedCopy.FileId == Guid.Empty || !savedFileIds.Add(mappedCopy.FileId))
                                        continue;

                                    await _gmailChangeProcessor.SaveMimeFileAsync(mappedCopy.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
                                }
                            }
                        }

                        return null;
                    }

                    if (await _gmailChangeProcessor.IsMailExistsInFolderAsync(baseMailCopy.Id, assignedFolder.Id).ConfigureAwait(false) ||
                        await _gmailChangeProcessor.IsMailExistsAsync(Account.Id, localDraftCopyUniqueId).ConfigureAwait(false))
                    {
                        _logger.Debug("Skipping duplicate remote draft {RemoteId} for local draft {LocalId}",
                            baseMailCopy.Id, localDraftCopyUniqueId);
                        return null;
                    }
                }
            }
        }

        // For Gmail, a single mail can have multiple labels (folders).
        // Each label requires a separate MailCopy entry in the database with:
        // - Same Id, UniqueId, FileId (shared across all copies)
        // - Different FolderId (one per label)
        // ARCHIVE label is excluded here as it's virtual and handled by MapArchivedMailsAsync
        if (message.LabelIds is not null)
        {
            // Generate shared identifiers that will be the same for all copies of this mail
            var sharedId = baseMailCopy.Id;
            var sharedFileId = baseMailCopy.FileId;

            foreach (var labelId in message.LabelIds)
            {
                // Skip ARCHIVE label - it's virtual and handled separately
                if (labelId == ServiceConstants.ARCHIVE_LABEL_ID)
                    continue;

                // Create a new MailCopy instance for each label to avoid shared reference issues
                var mailCopyForLabel = await CreateMinimalMailCopyAsync(message, assignedFolder, cancellationToken);

                if (mimeMessage != null)
                {
                    EnrichMailCopyFromMime(mailCopyForLabel, mimeMessage);
                }

                // Ensure all copies share the same Id and FileId
                mailCopyForLabel.Id = sharedId;
                mailCopyForLabel.FileId = sharedFileId;

                packageList.Add(new NewMailItemPackage(mailCopyForLabel, mimeMessage, labelId, extractedContacts));
            }
        }

        return packageList;
    }

    private async Task TryMapCalendarInvitationAsync(MailCopy baseMailCopy, MimeMessage mimeMessage, CancellationToken cancellationToken)
    {
        if (baseMailCopy == null || baseMailCopy.ItemType != MailItemType.CalendarInvitation || mimeMessage == null)
            return;

        var invitationUid = mimeMessage.ExtractInvitationUid();
        if (string.IsNullOrWhiteSpace(invitationUid))
            return;

        var calendars = await _gmailChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        if (calendars == null || calendars.Count == 0)
            return;

        foreach (var calendar in calendars)
        {
            try
            {
                var listRequest = _calendarService.Events.List(calendar.RemoteCalendarId);
                listRequest.ICalUID = invitationUid;
                listRequest.MaxResults = 1;
                listRequest.SingleEvents = false;

                var listResponse = await listRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                var matchedEvent = listResponse?.Items?.FirstOrDefault();
                if (matchedEvent == null || string.IsNullOrWhiteSpace(matchedEvent.Id))
                    continue;

                await _gmailChangeProcessor.ManageCalendarEventAsync(matchedEvent, calendar, Account).ConfigureAwait(false);

                var localCalendarItem = await _gmailChangeProcessor.GetCalendarItemAsync(calendar.Id, matchedEvent.Id).ConfigureAwait(false);
                if (localCalendarItem == null)
                    return;

                await _gmailChangeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping()
                {
                    Id = Guid.NewGuid(),
                    AccountId = Account.Id,
                    MailCopyId = baseMailCopy.Id,
                    InvitationUid = invitationUid,
                    CalendarId = calendar.Id,
                    CalendarItemId = localCalendarItem.Id,
                    CalendarRemoteEventId = matchedEvent.Id
                }).ConfigureAwait(false);

                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to map Gmail calendar invitation mail {MailCopyId} for calendar {CalendarId}", baseMailCopy.Id, calendar.Id);
            }
        }
    }

    #endregion

    #region Calendar Operations

    public override List<IRequestBundle<IGoogleApiRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var calendarItem = request.PreparedItem;
        var attendees = request.PreparedEvent.Attendees;
        var reminders = request.PreparedEvent.Reminders;
        var calendar = request.AssignedCalendar;

        var googleEvent = CreateGoogleCalendarEvent(
            calendarItem,
            attendees,
            reminders,
            includeEventId: true,
            includeStatus: true,
            includeEmptyRecurrence: false);

        var insertRequest = _calendarService.Events.Insert(googleEvent, calendar.RemoteCalendarId);
        insertRequest.SendUpdates = attendees.Count > 0
            ? global::Google.Apis.Calendar.v3.EventsResource.InsertRequest.SendUpdatesEnum.All
            : global::Google.Apis.Calendar.v3.EventsResource.InsertRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IGoogleApiRequest, Event>(insertRequest, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> AcceptEvent(AcceptEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot accept event without remote event ID");
        }

        // For Gmail, we need to patch the event with the user's response status
        // Get the current user's email from the account
        var userEmail = Account.Address;

        var patchRequest = _calendarService.Events.Patch(
            CreateGoogleRsvpPatch(userEmail, "accepted", request.ResponseMessage),
            calendar.RemoteCalendarId,
            remoteEventId);

        // Send updates to other attendees if there's a message
        patchRequest.SendUpdates = !string.IsNullOrEmpty(request.ResponseMessage)
            ? global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.All
            : global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IGoogleApiRequest>(patchRequest, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> DeclineEvent(DeclineEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot decline event without remote event ID");
        }

        var userEmail = Account.Address;

        var patchRequest = _calendarService.Events.Patch(
            CreateGoogleRsvpPatch(userEmail, "declined", request.ResponseMessage),
            calendar.RemoteCalendarId,
            remoteEventId);

        patchRequest.SendUpdates = !string.IsNullOrEmpty(request.ResponseMessage)
            ? global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.All
            : global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IGoogleApiRequest>(patchRequest, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> TentativeEvent(TentativeEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot tentatively accept event without remote event ID");
        }

        var userEmail = Account.Address;

        var patchRequest = _calendarService.Events.Patch(
            CreateGoogleRsvpPatch(userEmail, "tentative", request.ResponseMessage),
            calendar.RemoteCalendarId,
            remoteEventId);

        patchRequest.SendUpdates = !string.IsNullOrEmpty(request.ResponseMessage)
            ? global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.All
            : global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IGoogleApiRequest>(patchRequest, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var calendarItem = request.Item;
        var attendees = request.Attendees;
        var reminders = request.Reminders;

        // Get the calendar for this event
        var calendar = calendarItem.AssignedCalendar;
        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot update event without remote event ID");
        }

        var googleEvent = CreateGoogleCalendarEvent(
            calendarItem,
            attendees,
            reminders,
            includeEventId: false,
            includeStatus: false,
            includeEmptyRecurrence: true);

        // Patch preserves provider-managed fields (attachments, conference data and reminders
        // when they were not part of this update) while still allowing explicit empty lists
        // to clear attendees and recurrence.
        var updateRequest = _calendarService.Events.Patch(googleEvent, calendar.RemoteCalendarId, remoteEventId);

        // Removing the last attendee still requires a notification update.
        updateRequest.SendUpdates = (attendees?.Count > 0 || request.OriginalAttendees?.Count > 0)
            ? global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.All
            : global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IGoogleApiRequest>(updateRequest, request)];
    }

    public override List<IRequestBundle<IGoogleApiRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<IGoogleApiRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var calendarItem = request.Item;

        // Get the calendar for this event
        var calendar = calendarItem.AssignedCalendar;
        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot delete event without remote event ID");
        }

        var deleteRequest = _calendarService.Events.Delete(calendar.RemoteCalendarId, remoteEventId);

        // Send cancellation notifications to attendees
        deleteRequest.SendUpdates = global::Google.Apis.Calendar.v3.EventsResource.DeleteRequest.SendUpdatesEnum.All;

        return [new HttpRequestBundle<IGoogleApiRequest>(deleteRequest, request)];
    }

    #endregion

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();

        _gmailService.Dispose();
        if (!ReferenceEquals(_gmailFilterService, _gmailService))
            _gmailFilterService.Dispose();
        _peopleService.Dispose();
        _calendarService.Dispose();
        _driveService.Dispose();
        _googleHttpClient.Dispose();
        _googleProviderFeatureHttpClient?.Dispose();
    }

    private async Task UploadCalendarEventAttachmentsAsync(CreateCalendarEventRequest request, Event createdEvent, CancellationToken cancellationToken)
    {
        var composeAttachments = request.ComposeResult.Attachments ?? [];
        if (composeAttachments.Count == 0)
            return;

        if (composeAttachments.Count > 25)
            throw new InvalidOperationException("Google Calendar supports at most 25 attachments per event.");

        var eventAttachments = createdEvent.Attachments?
            .Where(attachment => attachment != null && !string.IsNullOrWhiteSpace(attachment.FileUrl))
            .ToList() ?? [];

        foreach (var attachment in composeAttachments.Where(a => !string.IsNullOrWhiteSpace(a.FilePath) && File.Exists(a.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventAttachments.Add(await UploadAttachmentToDriveAsync(attachment, cancellationToken).ConfigureAwait(false));
        }

        if (eventAttachments.Count == 0)
            return;

        var patchRequest = _calendarService.Events.Patch(new Event
        {
            Attachments = eventAttachments
        }, request.AssignedCalendar.RemoteCalendarId, createdEvent.Id);

        patchRequest.SupportsAttachments = true;
        patchRequest.SendUpdates = global::Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        await patchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<EventAttachment> UploadAttachmentToDriveAsync(
        Wino.Core.Domain.Models.Calendar.CalendarEventComposeAttachmentDraft attachment,
        CancellationToken cancellationToken)
    {
        var fileName = string.IsNullOrWhiteSpace(attachment.FileName)
            ? Path.GetFileName(attachment.FilePath)
            : attachment.FileName;
        var contentType = MimeTypes.GetMimeType(fileName);

        await using var fileStream = File.OpenRead(attachment.FilePath);

        var uploadRequest = _driveService.Files.Create(new DriveFile
        {
            Name = fileName,
            MimeType = contentType
        }, fileStream, contentType);
        uploadRequest.Fields = "id,name,mimeType,webViewLink";

        var uploadProgress = await uploadRequest.UploadAsync(cancellationToken).ConfigureAwait(false);

        if (uploadProgress.Status != GoogleUploadStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Failed to upload '{fileName}' to Google Drive. Upload status: {uploadProgress.Status}.");
        }

        var uploadedFile = uploadRequest.ResponseBody;
        if (uploadedFile == null || string.IsNullOrWhiteSpace(uploadedFile.Id) || string.IsNullOrWhiteSpace(uploadedFile.WebViewLink))
        {
            throw new InvalidOperationException($"Google Drive did not return a valid attachment link for '{fileName}'.");
        }

        return new EventAttachment
        {
            FileId = uploadedFile.Id,
            FileUrl = uploadedFile.WebViewLink,
            MimeType = uploadedFile.MimeType ?? contentType,
            Title = uploadedFile.Name ?? fileName
        };
    }

    private static Event CreateGoogleRsvpPatch(string attendeeEmail, string responseStatus, string comment)
        => new()
        {
            // Google patch replaces arrays wholesale unless attendeesOmitted is true.
            // Marking this as a partial attendee list preserves every other guest.
            AttendeesOmitted = true,
            Attendees =
            [
                new EventAttendee
                {
                    Email = attendeeEmail,
                    ResponseStatus = responseStatus,
                    Comment = comment
                }
            ]
        };

    private static Event CreateGoogleCalendarEvent(
        CalendarItem calendarItem,
        IReadOnlyCollection<CalendarEventAttendee> attendees,
        IReadOnlyCollection<Reminder> reminders,
        bool includeEventId,
        bool includeStatus,
        bool includeEmptyRecurrence)
    {
        var googleEvent = new Event
        {
            Id = includeEventId ? calendarItem.Id.ToString("N").ToLowerInvariant() : null,
            Summary = calendarItem.Title,
            Description = calendarItem.Description,
            Location = calendarItem.Location,
            // Event status is not the attendee's response status. Updates must leave it
            // untouched; RSVP changes use attendees[].responseStatus instead.
            Status = includeStatus ? "confirmed" : null,
            Transparency = calendarItem.ShowAs == CalendarItemShowAs.Free ? "transparent" : "opaque",
            Attendees = attendees?
                .Select(attendee => new EventAttendee
                {
                    Email = attendee.Email,
                    DisplayName = attendee.Name,
                    Optional = attendee.IsOptionalAttendee
                })
                .ToList()
        };

        if (calendarItem.IsAllDayEvent)
        {
            googleEvent.Start = new EventDateTime
            {
                Date = FormatGoogleCalendarDate(calendarItem.StartDate),
                TimeZone = NormalizeGoogleTimeZoneId(calendarItem.StartTimeZone)
            };
            googleEvent.End = new EventDateTime
            {
                Date = FormatGoogleCalendarDate(calendarItem.EndDate),
                TimeZone = NormalizeGoogleTimeZoneId(calendarItem.EndTimeZone ?? calendarItem.StartTimeZone)
            };
        }
        else
        {
            var startTimeZone = NormalizeGoogleTimeZoneId(calendarItem.StartTimeZone);
            var endTimeZoneId = calendarItem.EndTimeZone ?? calendarItem.StartTimeZone;
            var endTimeZone = NormalizeGoogleTimeZoneId(endTimeZoneId);

            googleEvent.Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(
                    DateTime.SpecifyKind(calendarItem.StartDate, DateTimeKind.Unspecified),
                    ResolveOffset(calendarItem.StartDate, calendarItem.StartTimeZone)),
                TimeZone = startTimeZone
            };
            googleEvent.End = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(
                    DateTime.SpecifyKind(calendarItem.EndDate, DateTimeKind.Unspecified),
                    ResolveOffset(calendarItem.EndDate, endTimeZoneId)),
                TimeZone = endTimeZone
            };
        }

        if (reminders != null)
        {
            googleEvent.Reminders = new Event.RemindersData
            {
                UseDefault = false,
                Overrides = reminders
                    .Select(reminder => new EventReminder
                    {
                        Method = reminder.ReminderType == CalendarItemReminderType.Email ? "email" : "popup",
                        Minutes = (int)Math.Max(0, reminder.DurationInSeconds / 60)
                    })
                    .ToList()
            };
        }

        // Recurrence is owned by the series master. Sending it on an occurrence can
        // make providers reject an otherwise valid instance edit.
        if (!calendarItem.IsRecurringChild &&
            (includeEmptyRecurrence || !string.IsNullOrWhiteSpace(calendarItem.Recurrence)))
        {
            googleEvent.Recurrence = string.IsNullOrWhiteSpace(calendarItem.Recurrence)
                ? []
                : calendarItem.Recurrence
                    .Split(Wino.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
        }

        return googleEvent;
    }

    private static TimeSpan ResolveOffset(DateTime dateTime, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeSpan.Zero;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(dateTime);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static string NormalizeGoogleTimeZoneId(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return timeZoneId;

        if (timeZoneId.Contains('/'))
            return timeZoneId;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaTimeZoneId))
            return ianaTimeZoneId;

        return timeZoneId;
    }

    #region Provider mail filters

    public async Task<IReadOnlyList<MailFilter>> GetProviderFiltersAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gmailFilterService.Users.Settings.Filters.List("me")
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return response?.Filter?
            .Where(filter => filter != null)
            .Select((filter, index) => ToMailFilter(filter, index))
            .ToList() ?? [];
    }

    public async Task<MailFilter> CreateProviderFilterAsync(
        MailFilter filter,
        CancellationToken cancellationToken = default)
    {
        var created = await _gmailFilterService.Users.Settings.Filters
            .Create(ToGmailFilter(filter), "me")
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return created == null
            ? throw new InvalidOperationException("Gmail did not return the created filter.")
            : CopyLocalMetadata(ToMailFilter(created, 0), filter);
    }

    public async Task<MailFilter> UpdateProviderFilterAsync(
        MailFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filter.RemoteId))
            throw new InvalidOperationException("The Gmail filter has no remote identifier.");

        // Gmail has no update endpoint. Create the replacement first, delete the old
        // filter second, and remove the replacement if the second operation fails.
        var created = await _gmailFilterService.Users.Settings.Filters
            .Create(ToGmailFilter(filter), "me")
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (created == null || string.IsNullOrWhiteSpace(created.Id))
            throw new InvalidOperationException("Gmail did not return the replacement filter.");

        try
        {
            await DeleteProviderFilterAsync(filter.RemoteId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await DeleteProviderFilterAsync(created.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                _logger.Error(
                    rollbackException,
                    "Failed to roll back replacement Gmail filter {FilterId} for account {AccountId}.",
                    created.Id,
                    Account.Id);
            }

            throw;
        }

        return CopyLocalMetadata(ToMailFilter(created, 0), filter);
    }

    public async Task DeleteProviderFilterAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteId))
            throw new ArgumentException("A remote Gmail filter identifier is required.", nameof(remoteId));

        await _gmailFilterService.Users.Settings.Filters.Delete("me", remoteId)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static MailFilter ToMailFilter(GmailFilter gmailFilter, int index)
    {
        var filter = new MailFilter
        {
            ManagementType = MailFilterManagementType.Provider,
            RemoteId = gmailFilter.Id,
            Name = $"Gmail filter {index + 1}",
            IsEnabled = true,
            Sequence = index,
            IsReadOnly = true,
            ProviderSummary = BuildGmailSummary(gmailFilter)
        };

        var criteria = gmailFilter.Criteria;
        if (!string.IsNullOrWhiteSpace(criteria?.From))
            AddGmailCondition(filter, MailFilterConditionField.FromAddress, criteria.From);
        if (!string.IsNullOrWhiteSpace(criteria?.Subject))
            AddGmailCondition(filter, MailFilterConditionField.Subject, criteria.Subject);
        if (!string.IsNullOrWhiteSpace(criteria?.Query))
            AddGmailCondition(filter, MailFilterConditionField.PreviewText, criteria.Query);
        if (criteria?.HasAttachment is bool hasAttachment)
        {
            filter.Conditions.Add(new MailFilterCondition
            {
                Field = MailFilterConditionField.HasAttachments,
                Operator = MailFilterConditionOperator.Equals,
                Value = hasAttachment.ToString()
            });
        }

        var addLabels = gmailFilter.Action?.AddLabelIds?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removeLabels = gmailFilter.Action?.RemoveLabelIds?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (removeLabels.Contains("UNREAD"))
            AddGmailAction(filter, MailFilterActionType.MarkRead);
        else if (addLabels.Contains("UNREAD"))
            AddGmailAction(filter, MailFilterActionType.MarkUnread);

        if (addLabels.Contains("STARRED"))
            AddGmailAction(filter, MailFilterActionType.SetFlag);
        else if (removeLabels.Contains("STARRED"))
            AddGmailAction(filter, MailFilterActionType.ClearFlag);

        if (addLabels.Contains("TRASH"))
            AddGmailAction(filter, MailFilterActionType.SoftDelete);
        else if (addLabels.Contains("SPAM"))
            AddGmailAction(filter, MailFilterActionType.MoveToJunk);
        else if (removeLabels.Contains("SPAM"))
            AddGmailAction(filter, MailFilterActionType.MarkAsNotJunk);
        else if (removeLabels.Contains("INBOX"))
        {
            var targetLabel = addLabels.FirstOrDefault(label => !IsStateLabel(label));
            AddGmailAction(
                filter,
                targetLabel == null ? MailFilterActionType.Archive : MailFilterActionType.Move,
                targetLabel);
        }

        return filter;
    }

    private static GmailFilter ToGmailFilter(MailFilter filter)
    {
        if (filter.StopProcessing || filter.MatchMode != MailFilterMatchMode.All)
            throw new NotSupportedException("Gmail filters do not support stop processing or match-any behavior.");

        var criteria = new FilterCriteria();
        var queryParts = new List<string>();
        foreach (var condition in filter.Conditions.OrderBy(condition => condition.Order))
        {
            if (!IsGmailFilterConditionSupported(condition))
            {
                throw new NotSupportedException(
                    $"{condition.Field} with {condition.Operator} is not supported by Gmail filters.");
            }

            switch (condition.Field)
            {
                case MailFilterConditionField.FromAddress:
                    criteria.From = condition.Value;
                    break;
                case MailFilterConditionField.Subject:
                    criteria.Subject = condition.Value;
                    break;
                case MailFilterConditionField.PreviewText:
                    queryParts.Add(condition.Value);
                    break;
                case MailFilterConditionField.HasAttachments:
                    criteria.HasAttachment = bool.TryParse(condition.Value, out var hasAttachment) && hasAttachment;
                    break;
                case MailFilterConditionField.Importance:
                    queryParts.Add(string.Equals(condition.Value, "High", StringComparison.OrdinalIgnoreCase)
                        ? "is:important"
                        : "-is:important");
                    break;
                default:
                    throw new NotSupportedException($"{condition.Field} is not supported by Gmail filters.");
            }
        }

        if (queryParts.Count > 0)
            criteria.Query = string.Join(" ", queryParts);

        var addLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removeLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in filter.Actions.OrderBy(action => action.Order))
        {
            switch (action.Type)
            {
                case MailFilterActionType.MarkRead:
                    removeLabels.Add("UNREAD");
                    break;
                case MailFilterActionType.MarkUnread:
                    addLabels.Add("UNREAD");
                    break;
                case MailFilterActionType.SetFlag:
                    addLabels.Add("STARRED");
                    break;
                case MailFilterActionType.ClearFlag:
                    removeLabels.Add("STARRED");
                    break;
                case MailFilterActionType.Move:
                    removeLabels.Add("INBOX");
                    addLabels.Add(action.TargetRemoteFolderId);
                    break;
                case MailFilterActionType.Archive:
                    removeLabels.Add("INBOX");
                    break;
                case MailFilterActionType.MoveToJunk:
                    removeLabels.Add("INBOX");
                    addLabels.Add("SPAM");
                    break;
                case MailFilterActionType.MarkAsNotJunk:
                    removeLabels.Add("SPAM");
                    addLabels.Add("INBOX");
                    break;
                case MailFilterActionType.SoftDelete:
                    removeLabels.Add("INBOX");
                    addLabels.Add("TRASH");
                    break;
                default:
                    throw new NotSupportedException($"{action.Type} is not supported by Gmail filters.");
            }
        }

        return new GmailFilter
        {
            Criteria = criteria,
            Action = new FilterAction
            {
                AddLabelIds = addLabels.Count == 0 ? null : addLabels.ToList(),
                RemoveLabelIds = removeLabels.Count == 0 ? null : removeLabels.ToList()
            }
        };
    }

    private static MailFilter CopyLocalMetadata(MailFilter providerFilter, MailFilter source)
    {
        providerFilter.Id = source.Id;
        providerFilter.MailAccountId = source.MailAccountId;
        providerFilter.Name = source.Name;
        providerFilter.IsWinoCreated = source.IsWinoCreated;
        providerFilter.IsReadOnly = false;
        providerFilter.CreatedAtUtc = source.CreatedAtUtc;
        return providerFilter;
    }

    private static void AddGmailCondition(
        MailFilter filter,
        MailFilterConditionField field,
        string value)
        => filter.Conditions.Add(new MailFilterCondition
        {
            Field = field,
            Operator = MailFilterConditionOperator.Contains,
            Value = value
        });

    private static void AddGmailAction(
        MailFilter filter,
        MailFilterActionType type,
        string targetRemoteFolderId = null)
        => filter.Actions.Add(new MailFilterAction
        {
            Type = type,
            TargetRemoteFolderId = targetRemoteFolderId
        });

    private static bool IsStateLabel(string label)
        => label is "INBOX" or "UNREAD" or "STARRED" or "IMPORTANT" or "TRASH" or "SPAM";

    private static bool IsGmailFilterConditionSupported(MailFilterCondition condition)
        => condition.Field switch
        {
            MailFilterConditionField.FromAddress
                or MailFilterConditionField.Subject
                or MailFilterConditionField.PreviewText => condition.Operator == MailFilterConditionOperator.Contains,
            MailFilterConditionField.HasAttachments
                or MailFilterConditionField.Importance => condition.Operator == MailFilterConditionOperator.Equals,
            _ => false
        };

    private static string BuildGmailSummary(GmailFilter filter)
    {
        var criteriaCount = new[]
        {
            filter.Criteria?.From,
            filter.Criteria?.Subject,
            filter.Criteria?.Query,
            filter.Criteria?.NegatedQuery
        }.Count(value => !string.IsNullOrWhiteSpace(value))
            + (filter.Criteria?.HasAttachment.HasValue == true ? 1 : 0);
        var actionCount = (filter.Action?.AddLabelIds?.Count ?? 0)
            + (filter.Action?.RemoveLabelIds?.Count ?? 0)
            + (!string.IsNullOrWhiteSpace(filter.Action?.Forward) ? 1 : 0);
        return string.Format(Translator.MailFilters_ProviderSummary, criteriaCount, actionCount);
    }

    #endregion
}
