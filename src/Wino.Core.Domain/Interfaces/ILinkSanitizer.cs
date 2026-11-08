using System;

namespace Wino.Core.Domain.Interfaces;

public interface ILinkSanitizer
{
    /// <summary>
    /// Removes known tracking parameters from an http or https link. Returns the same instance when nothing was removed.
    /// </summary>
    Uri Clean(Uri uri, out int removedCount);
}
