using System;

namespace Wino.Core.Domain.Models.Attachments;

/// <summary>A user declined the platform attachment policy prompt.</summary>
public sealed class AttachmentPolicyCancellationException(string message) : OperationCanceledException(message);
