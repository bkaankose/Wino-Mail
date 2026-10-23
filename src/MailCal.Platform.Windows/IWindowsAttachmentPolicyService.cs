#nullable enable
using System;

namespace MailCal.Platform.Windows;

public interface IWindowsAttachmentPolicyService
{
    void ApplySavePolicy(string localPath);
    void Execute(string localPath, IntPtr ownerWindow);
}
