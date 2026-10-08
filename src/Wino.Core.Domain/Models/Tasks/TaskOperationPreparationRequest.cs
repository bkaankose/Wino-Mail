using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Tasks;

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
