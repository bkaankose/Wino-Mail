using System;

namespace Wino.Core.Domain.Exceptions
{
    /// <summary>
    /// An exception thrown when the background task execution policies are denied for some reason.
    /// </summary>
    public class BackgroundTaskExecutionRequestDeniedException : Exception { }
}
