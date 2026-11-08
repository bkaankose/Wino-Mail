#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoBillingService
{
    Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(
        WinoAddOnProductType productType,
        CancellationToken cancellationToken = default);

    Task<bool> OpenCheckoutAsync(WinoAddOnProductType productType, CancellationToken cancellationToken = default);

    /// <summary>Opens the Stripe Customer Portal for the signed-in Wino Account in the default browser.</summary>
    Task<bool> OpenCustomerPortalAsync(CancellationToken cancellationToken = default);

    Task<ApiEnvelope<BillingStatusResultDto>> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> HasUnlimitedAccountsAsync(CancellationToken cancellationToken = default);
}
