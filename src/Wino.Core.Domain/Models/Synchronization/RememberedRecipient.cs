using System;

namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>
/// One address a mailbox has written to before, without any of the provider's storage format.
/// </summary>
/// <param name="Address">The address to send to.</param>
/// <param name="DisplayName">What to show for it, which may be the address again.</param>
/// <param name="Weight">
/// How strongly the provider ranks it - higher is offered first. Zero when the caller is recording a
/// new send and has no opinion; the provider decides what that becomes.
/// </param>
public readonly record struct RememberedRecipient(string Address, string? DisplayName, int Weight);
