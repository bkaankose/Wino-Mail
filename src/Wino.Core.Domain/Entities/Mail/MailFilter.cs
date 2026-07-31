using System;
using System.Collections.Generic;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Mail;

public class MailFilter
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid MailAccountId { get; set; }

    public MailFilterManagementType ManagementType { get; set; }

    public string RemoteId { get; set; }

    public string Name { get; set; }

    /// <summary>
    /// Remote provider folder identifier. Required for Wino-local rules.
    /// Provider rules may leave this null when the provider owns the trigger scope.
    /// </summary>
    public string SourceRemoteFolderId { get; set; }

    public MailFilterMatchMode MatchMode { get; set; } = MailFilterMatchMode.All;

    public bool IsEnabled { get; set; } = true;

    public int Sequence { get; set; }

    public bool StopProcessing { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsWinoCreated { get; set; }

    public bool ProviderHasError { get; set; }

    public string ProviderSummary { get; set; }

    public string ProviderDefinitionJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    [Ignore]
    public List<MailFilterCondition> Conditions { get; set; } = [];

    [Ignore]
    public List<MailFilterAction> Actions { get; set; } = [];

    [Ignore]
    public bool HasMissingFolder { get; set; }

    [Ignore]
    public string SourceFolderName { get; set; }

    [Ignore]
    public string StatusText { get; set; }
}

public class MailFilterCondition
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid MailFilterId { get; set; }

    public int Order { get; set; }

    public MailFilterConditionField Field { get; set; }

    public MailFilterConditionOperator Operator { get; set; }

    /// <summary>
    /// Canonical invariant operand. Boolean values use true/false and enums use their names.
    /// </summary>
    public string Value { get; set; }
}

public class MailFilterAction
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid MailFilterId { get; set; }

    public int Order { get; set; }

    public MailFilterActionType Type { get; set; }

    /// <summary>
    /// Required for actions that change the message's folder.
    /// </summary>
    public string TargetRemoteFolderId { get; set; }
}

public class MailFilterExecution
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid MailFilterId { get; set; }

    public Guid MailAccountId { get; set; }

    public string RemoteMessageId { get; set; }

    public string SourceRemoteFolderId { get; set; }

    public MailFilterExecutionState State { get; set; }

    public int AttemptCount { get; set; }

    public string LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
