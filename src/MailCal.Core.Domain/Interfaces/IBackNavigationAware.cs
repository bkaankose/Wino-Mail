#nullable enable

using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Implemented by view models that need to react when the frame returns to them.
/// The original navigation parameter is replayed by the frame; <paramref name="result"/>
/// carries whatever the page being left behind published.
/// </summary>
public interface IBackNavigationAware
{
    void OnNavigatedBack(object? parameter, NavigationResult? result);
}
