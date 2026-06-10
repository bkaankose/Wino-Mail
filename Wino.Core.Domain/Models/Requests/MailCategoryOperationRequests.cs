using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Requests;

/// <summary>
/// A message whose remote category names must be rewritten as part of a category change.
/// </summary>
/// <param name="MessageId">Remote mail copy id.</param>
/// <param name="CategoryNames">Full set of category names the message should end up with.</param>
public record MailCategoryMessageUpdateTarget(string MessageId, IReadOnlyList<string> CategoryNames);

/// <summary>
/// Serializable descriptor for creating, updating or deleting a mail category remotely.
/// Crosses the RPC pipe; the companion maps it to the matching synchronizer request.
/// </summary>
public record MailCategoryOperationRequest(
    Guid AccountId,
    MailCategoryChangeType ChangeType,
    MailCategory Category,
    string PreviousName = null,
    string PreviousRemoteId = null,
    List<MailCategoryMessageUpdateTarget> AffectedMessages = null);

/// <summary>
/// A single mail item affected by a category assignment toggle.
/// </summary>
/// <param name="Item">The mail copy whose categories change.</param>
/// <param name="CategoryNames">Full set of category names assigned to the message after the change.</param>
public record MailCategoryAssignmentTarget(MailCopy Item, List<string> CategoryNames);

/// <summary>
/// Serializable descriptor for assigning/unassigning a category to a batch of mails.
/// Crosses the RPC pipe; the companion maps each target to a synchronizer request.
/// </summary>
public record MailCategoryAssignmentOperationRequest(
    Guid AccountId,
    Guid CategoryId,
    string CategoryName,
    bool IsAssigned,
    List<MailCategoryAssignmentTarget> Targets);
