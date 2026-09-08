#nullable enable
using System;

namespace Wino.Services;

internal interface IWindowsAttachmentPolicyService
{
    void ApplySavePolicy(string localPath);
    void Execute(string localPath, IntPtr ownerWindow);
}
