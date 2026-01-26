using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Requests;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Adapters;

namespace Wino.Core.Synchronizers.Examples;

/// <summary>
/// Example showing how to integrate the new request execution system into OutlookSynchronizer.
/// This is a REFERENCE IMPLEMENTATION - actual integration should be done in OutlookSynchronizer.cs
/// </summary>
public class OutlookSynchronizerIntegrationExample
{
    private readonly GraphServiceClient _graphClient;
    private readonly RequestExecutionEngine _executionEngine;
    private readonly OutlookRequestExecutionAdapter _requestAdapter;

    public OutlookSynchronizerIntegrationExample(GraphServiceClient graphClient)
    {
        _graphClient = graphClient;
        _executionEngine = new RequestExecutionEngine();
        _requestAdapter = new OutlookRequestExecutionAdapter(graphClient, _executionEngine);
    }

    /// <summary>
    /// NEW IMPLEMENTATION of ExecuteNativeRequestsAsync using the refactored system.
    /// This replaces the old method in OutlookSynchronizer.
    /// </summary>
    public async Task ExecuteNativeRequestsAsync(
        List<IExecutableRequest> executableRequests,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        // Create execution context
        var context = new RequestExecutionContext(
            accountId,
            cancellationToken,
            null); // Services can be injected via IServiceProvider if needed

        // Execute all requests using the adapter
        var results = await _requestAdapter.ExecuteBatchAsync(executableRequests, context);

        // Process results
        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        // Log summary
        Console.WriteLine($"Executed {results.Count} requests: {successCount} succeeded, {failureCount} failed");

        // Log failures
        foreach (var failure in results.Where(r => !r.IsSuccess))
        {
            Console.WriteLine($"Failed request: {failure.Request.GetType().Name} - {failure.Error.Message}");
        }

        // If any request failed with a critical error, you can choose to throw
        if (results.Any(r => !r.IsSuccess && IsCriticalError(r.Error)))
        {
            var criticalError = results.First(r => !r.IsSuccess && IsCriticalError(r.Error));
            throw new InvalidOperationException(
                $"Critical error in {criticalError.Request.GetType().Name}: {criticalError.Error.Message}",
                criticalError.Error);
        }
    }

    private bool IsCriticalError(Exception error)
    {
        // Define what constitutes a critical error that should stop execution
        return error is UnauthorizedAccessException ||
               error is InvalidOperationException && error.Message.Contains("token");
    }
}

/// <summary>
/// Example showing how to create Outlook-specific executable requests.
/// These would replace the old request methods in OutlookSynchronizer.
/// </summary>
public static class OutlookRequestFactory
{
    /// <summary>
    /// Creates an executable MarkRead request for Outlook.
    /// </summary>
    public static IExecutableRequest CreateMarkReadRequest(MailCopy item, bool isRead)
    {
        return new OutlookMarkReadRequest(item, isRead);
    }

    /// <summary>
    /// Creates an executable Delete request for Outlook.
    /// </summary>
    public static IExecutableRequest CreateDeleteRequest(MailCopy item)
    {
        return new OutlookDeleteRequest(item);
    }

    /// <summary>
    /// Creates an executable Move request for Outlook.
    /// </summary>
    public static IExecutableRequest CreateMoveRequest(MailCopy item, MailItemFolder fromFolder, MailItemFolder toFolder)
    {
        return new OutlookMoveRequest(item, fromFolder, toFolder);
    }
}

/// <summary>
/// Outlook-specific implementation of MarkReadRequest.
/// </summary>
public record OutlookMarkReadRequest(MailCopy Item, bool IsRead)
    : ExecutableMailRequestBase<RequestInformation>(Item)
{
    public override Domain.Enums.MailSynchronizerOperation Operation => Domain.Enums.MailSynchronizerOperation.MarkRead;

    public override Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        // Create Outlook-specific request
        var message = new Microsoft.Graph.Models.Message
        {
            IsRead = IsRead
        };

        var requestInfo = new RequestInformation
        {
            HttpMethod = Microsoft.Kiota.Abstractions.Method.PATCH,
            URI = new Uri($"https://graph.microsoft.com/v1.0/me/messages/{Item.Id}")
        };

        // Set content (simplified - real implementation would use proper serialization)
        requestInfo.SetContentFromParsable(
            (Microsoft.Kiota.Abstractions.IRequestAdapter)null,
            "application/json",
            message);

        return Task.FromResult(requestInfo);
    }

    public override void ApplyUIChanges()
    {
        Item.IsRead = IsRead;
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailUpdatedMessage(Item), 0);
    }

    public override void RevertUIChanges()
    {
        Item.IsRead = !IsRead;
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailUpdatedMessage(Item), 0);
    }
}

/// <summary>
/// Outlook-specific implementation of DeleteRequest.
/// </summary>
public record OutlookDeleteRequest(MailCopy Item)
    : ExecutableMailRequestBase<RequestInformation>(Item)
{
    public override Domain.Enums.MailSynchronizerOperation Operation => Domain.Enums.MailSynchronizerOperation.Delete;

    public override Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        var requestInfo = new RequestInformation
        {
            HttpMethod = Microsoft.Kiota.Abstractions.Method.DELETE,
            URI = new Uri($"https://graph.microsoft.com/v1.0/me/messages/{Item.Id}")
        };

        return Task.FromResult(requestInfo);
    }

    public override void ApplyUIChanges()
    {
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailRemovedMessage(Item), 0);
    }

    public override void RevertUIChanges()
    {
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailAddedMessage(Item), 0);
    }
}

/// <summary>
/// Outlook-specific implementation of MoveRequest with async folder lookup.
/// This demonstrates the power of async PrepareNativeRequestAsync.
/// </summary>
public record OutlookMoveRequest(MailCopy Item, MailItemFolder FromFolder, MailItemFolder ToFolder)
    : ExecutableMailRequestBase<RequestInformation>(Item)
{
    private Guid _originalFolderId;

    public override Domain.Enums.MailSynchronizerOperation Operation => Domain.Enums.MailSynchronizerOperation.Move;

    public override async Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        // Store original for rollback
        _originalFolderId = Item.FolderId;

        // Example: If ToFolder.RemoteFolderId is null, we could query it from database
        // This is the async capability that the old system couldn't support
        if (string.IsNullOrEmpty(ToFolder.RemoteFolderId))
        {
            // In real implementation, inject IFolderService via context.Services
            // var folderService = context.Services.GetService<IFolderService>();
            // var folder = await folderService.GetFolderAsync(ToFolder.Id);
            // ToFolder.RemoteFolderId = folder.RemoteFolderId;
            throw new InvalidOperationException("ToFolder.RemoteFolderId is required");
        }

        // Create move request body
        var moveBody = new Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody
        {
            DestinationId = ToFolder.RemoteFolderId
        };

        var requestInfo = new RequestInformation
        {
            HttpMethod = Microsoft.Kiota.Abstractions.Method.POST,
            URI = new Uri($"https://graph.microsoft.com/v1.0/me/messages/{Item.Id}/move")
        };

        // Set content
        requestInfo.SetContentFromParsable(
            (Microsoft.Kiota.Abstractions.IRequestAdapter)null,
            "application/json",
            moveBody);

        return requestInfo;
    }

    public override void ApplyUIChanges()
    {
        // Remove from old folder
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailRemovedMessage(Item), 0);

        // Update folder
        Item.FolderId = ToFolder.Id;
        Item.AssignedFolder = ToFolder;

        // Add to new folder
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailAddedMessage(Item), 0);
    }

    public override void RevertUIChanges()
    {
        // Remove from current folder
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailRemovedMessage(Item), 0);

        // Restore original
        Item.FolderId = _originalFolderId;
        Item.AssignedFolder = FromFolder;

        // Add back to original folder
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailAddedMessage(Item), 0);
    }
}

/// <summary>
/// Example of SendDraft request with response handling.
/// This captures the created message to avoid re-syncing folders.
/// </summary>
public record OutlookSendDraftRequest(
    MailCopy DraftItem,
    MailItemFolder DraftFolder,
    MailItemFolder SentFolder)
    : ExecutableMailRequestBase<RequestInformation, Microsoft.Graph.Models.Message>(DraftItem)
{
    public override Domain.Enums.MailSynchronizerOperation Operation => Domain.Enums.MailSynchronizerOperation.Send;

    public override Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        var requestInfo = new RequestInformation
        {
            HttpMethod = Microsoft.Kiota.Abstractions.Method.POST,
            URI = new Uri($"https://graph.microsoft.com/v1.0/me/messages/{Item.Id}/send")
        };

        return Task.FromResult(requestInfo);
    }

    public override async Task HandleResponseAsync(Microsoft.Graph.Models.Message response, IRequestExecutionContext context)
    {
        // This is where we capture the created message!
        // The API returns the sent message with its new ID in the Sent folder
        // We can process it directly instead of re-syncing the entire Sent folder

        if (response != null)
        {
            // In real implementation, inject IMail Service via context.Services
            // var mailService = context.Services.GetService<IMailService>();
            // await mailService.ProcessSentMessageAsync(response, SentFolder, context.AccountId);

            Console.WriteLine($"Captured sent message: {response.Id}");
            Console.WriteLine("No need to re-sync Sent folder!");
        }
    }

    public override void ApplyUIChanges()
    {
        // Remove draft from UI
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailRemovedMessage(Item), 0);
    }

    public override void RevertUIChanges()
    {
        // Restore draft to UI
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new Wino.Messaging.UI.MailAddedMessage(Item), 0);
    }
}
