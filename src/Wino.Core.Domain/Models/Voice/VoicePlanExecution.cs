#nullable enable
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>
/// A step that needs a second confirmation before anything runs: a large or permanent delete, emptying a folder, or a
/// large archive. <see cref="MarkedText"/> marks the strong parts with double asterisks; <see cref="ButtonText"/> names
/// the action for the button that confirms it ("Delete 57 mails").
/// </summary>
public sealed record VoicePlanRisk(int CommandIndex, string MarkedText, bool IsPermanent, string ButtonText);

/// <summary>
/// What running a voice plan did, in the user's words: one line per finished step (strong parts marked), the steps that
/// failed, and a summary the user asked for.
/// </summary>
public sealed record VoicePlanExecutionResult(
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> Failed,
    string? Summary);
