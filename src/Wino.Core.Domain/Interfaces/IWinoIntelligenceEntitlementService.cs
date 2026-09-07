#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoIntelligenceEntitlementService
{
    WinoIntelligenceEntitlementSnapshot Current { get; }
    Task<WinoIntelligenceEntitlementSnapshot> GetAsync(CancellationToken cancellationToken = default);
    Task<WinoIntelligenceEntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    void SetSignedOut();
}
