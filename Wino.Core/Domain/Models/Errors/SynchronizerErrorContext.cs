using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Errors;

/// <summary>
/// Contains context information about a synchronizer error
/// </summary>
public class SynchronizerErrorContext
{
    /// <summary>
    /// Account associated with the error
    /// </summary>
    public MailAccount Account { get; set; }

    /// <summary>
    /// Gets or sets the error code
    /// </summary>
    public int? ErrorCode { get; set; }

    /// <summary>
    /// Gets or sets the error message
    /// </summary>
    public string ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the request bundle associated with the error
    /// </summary>
    public IRequestBundle RequestBundle { get; set; }

    /// <summary>
    /// Gets or sets additional data associated with the error
    /// </summary>
    public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// Gets or sets the exception associated with the error
    /// </summary>
    public Exception Exception { get; set; }
}
