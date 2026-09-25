using Wino.Core.Domain.Interfaces;
using Wino.Intelligence.ConsoleApp.Hosting;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

internal static class ConsentScenarios
{
    public static async Task ReviewAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var consent = await ConsoleOutput.TimedAsync("GET consent",
            () => context.Get<IWinoAccountApiClient>().GetIntelligenceConsentAsync(cancellationToken)).ConfigureAwait(false);
        IntelligenceFlow.PrintConsent(consent);

        if (!IntelligenceFlow.IsCurrent(consent))
            await IntelligenceFlow.EnsureConsentAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Declining consent revokes it on the server, which deletes every job of the Wino user, and
    /// clears local intelligence for every mail account on this device.
    /// </summary>
    public static async Task RevokeAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        if (context.ApiTarget == ApiTarget.Production)
            ConsoleOutput.Warning("This is the production API. Revoking deletes the Wino user's server-side jobs there.");

        if (!ConsoleOutput.Confirm("Revoke Wino Intelligence consent and clear local intelligence for every account?"))
            return;

        var consent = await IntelligenceFlow.RevokeConsentAsync(context, cancellationToken).ConfigureAwait(false);
        IntelligenceFlow.PrintConsent(consent);
        ConsoleOutput.Success("Consent revoked.");
    }
}
