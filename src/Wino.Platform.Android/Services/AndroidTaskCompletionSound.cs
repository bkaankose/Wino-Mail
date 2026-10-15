using Android.Content;
using Android.Media;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Services;

/// <summary>The system click when a task is completed; silent when the user turned touch sounds off.</summary>
public sealed class AndroidTaskCompletionSound(Context context) : ITaskCompletionSound
{
    public void Play()
    {
        if (context.GetSystemService(Context.AudioService) is AudioManager audio)
            audio.PlaySoundEffect(SoundEffect.KeyClick);
    }
}
