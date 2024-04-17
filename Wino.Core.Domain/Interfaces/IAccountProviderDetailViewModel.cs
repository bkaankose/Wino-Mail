using System;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountProviderDetailViewModel
    {
        /// <summary>
        /// Entity id that will help to identify the startup entity on launch.
        /// </summary>
        Guid StartupEntityId { get; }

        /// <summary>
        /// Name representation of the view model that will be used to identify the startup entity on launch.
        /// </summary>
        string StartupEntityTitle { get; }
    }
}
