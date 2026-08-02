using System;
using System.Runtime.InteropServices;
using Wino.Core.Domain.Enums;

namespace Wino.Helpers;

internal static class NotificationSoundPlayer
{
    private const uint SoundAsync = 0x0001;
    private const uint SoundNoDefault = 0x0002;
    private const uint SoundAlias = 0x00010000;

    public static void Play(NotificationSoundEvent soundEvent)
    {
        var soundAlias = soundEvent switch
        {
            NotificationSoundEvent.IM => "Notification.IM",
            NotificationSoundEvent.Mail => "Notification.Mail",
            NotificationSoundEvent.Reminder => "Notification.Reminder",
            NotificationSoundEvent.SMS => "Notification.SMS",
            _ => "Notification.Default"
        };

        PlaySound(soundAlias, IntPtr.Zero, SoundAsync | SoundNoDefault | SoundAlias);
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string soundName, IntPtr moduleHandle, uint flags);
}
