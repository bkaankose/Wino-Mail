using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Helpers;

namespace Wino.Services;

public sealed class TaskCompletionSoundPlayer : ITaskCompletionSoundPlayer
{
    public void Play() => NotificationSoundPlayer.Play(NotificationSoundEvent.Default);
}
