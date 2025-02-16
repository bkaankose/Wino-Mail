using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IStartupBehaviorService
    {
        /// <summary>
        /// Gets whether Wino Server is set to launch on startup or not.
        /// </summary>
        Task<StartupBehaviorResult> GetCurrentStartupBehaviorAsync();

        /// <summary>
        /// Enables/disables the current startup behavior for Wino Server.
        /// </summary>
        /// <param name="isEnabled">Whether to launch enabled or disabled.</param>
        /// <returns>True if operation success, false if not.</returns>
        Task<StartupBehaviorResult> ToggleStartupBehavior(bool isEnabled);
    }
}
