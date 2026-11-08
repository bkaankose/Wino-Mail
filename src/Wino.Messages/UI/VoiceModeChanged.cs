using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Messaging.UI;

/// <summary>The Wino Account's voice mode changed, on the server or as a voice request reported it.</summary>
public sealed record VoiceModeChanged(VoiceMode Mode) : UIMessageBase<VoiceModeChanged>;
