using Wino.Core.Domain.Models.Voice;

namespace Wino.Core.Domain.Messaging.UI;

/// <summary>Asks the app to open the assistant for typing about the items the user selected.</summary>
public sealed record VoiceSelectionRequested(VoiceLocalSelection Selection);
