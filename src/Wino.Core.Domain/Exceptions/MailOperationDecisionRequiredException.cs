using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// A mail operation reached the backend without an answer it needs. The ViewModel that starts the operation asks
/// the question and passes the answer on the preparation request; the backend never asks.
/// </summary>
public class MailOperationDecisionRequiredException(MailOperationDecision decision)
    : Exception($"The mail operation needs an answer before it can run: {decision}.")
{
    public MailOperationDecision Decision { get; } = decision;
}
