using System;
using Wino.Core.Domain.Enums;

namespace Wino.Ipc.Contracts;

// Wire-state records for the domain exceptions that cross the pipe. Public so the
// source-generated WinoIpcJsonContext (separate assembly) can register them.

public sealed record InteractiveAuthRequiredState(Guid AccountId, string? Message);

public sealed record UnavailableSpecialFolderState(SpecialFolderType SpecialFolderType, Guid AccountId);

public sealed record InvalidMoveTargetState(InvalidMoveTargetReason Reason);
