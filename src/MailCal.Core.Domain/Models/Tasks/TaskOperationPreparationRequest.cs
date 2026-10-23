using System;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Tasks;

public sealed record TaskOperationPreparationRequest(
    Guid AccountId,
    TaskSynchronizerOperation Operation,
    AccountTaskList List = null,
    AccountTask Task = null,
    AccountTaskStep Step = null,
    AccountTaskList OriginalList = null,
    AccountTask OriginalTask = null,
    AccountTaskStep OriginalStep = null,
    AccountTaskListGroup Group = null,
    AccountTaskListGroup OriginalGroup = null);
