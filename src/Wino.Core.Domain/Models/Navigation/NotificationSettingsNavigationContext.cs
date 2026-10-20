namespace Wino.Core.Domain.Models.Navigation;

/// <summary>
/// Opens Settings > Notifications from the upgrade introduction. With <paramref name="OfferInstantPush"/>, the page turns
/// instant push on through its own toggle, which shows the privacy policy first when it has not been accepted yet.
/// </summary>
public sealed record NotificationSettingsNavigationContext(bool OfferInstantPush);
