namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Interface for all messages to report UI changes from synchronizers to UI.
/// None of these messages can't run a code that manipulates database.
/// They are sent either from processor or view models to signal some other
/// parts of the application.
/// </summary>

public interface IUIMessage;
