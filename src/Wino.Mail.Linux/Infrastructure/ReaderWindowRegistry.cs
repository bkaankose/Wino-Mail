using Wino.Mail.Linux.Views;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The reader windows (a popped-out message or an opened <c>.eml</c> file) over <see cref="WindowManager"/>, which
/// tracks them by key and closes them on stop; and the composer pop-out a popped-out reader asked for. Main thread.
/// </summary>
internal sealed class ReaderWindowRegistry(WindowManager windows)
{
    public const string Kind = "reader";
    public const int DefaultWidth = 820;
    public const int DefaultHeight = 640;

    private readonly HashSet<Guid> _composerPopOuts = [];

    public WindowManager Windows => windows;

    public static string MailKey(Guid fileId) => $"mail:{fileId:N}";

    // Ordinal like every WindowManager key: Linux paths are case-sensitive.
    public static string FileKey(string path) => $"file:{Path.GetFullPath(path)}";

    public bool TryPresent(string key) => windows.TryPresent(key);

    public SecondaryWindow? Find(string key) => windows.Find(key);

    public string[] Describe() => [.. windows.Describe().Where(static line => line.StartsWith("mail:", StringComparison.Ordinal) || line.StartsWith("file:", StringComparison.Ordinal))];

    /// <summary>A popped-out reader started Reply, Reply all or Forward: the composer for this draft pops out too (ED-10).</summary>
    public void RequestComposerPopOut(Guid draftUniqueId) => _composerPopOuts.Add(draftUniqueId);

    public bool TakeComposerPopOut(Guid draftUniqueId) => _composerPopOuts.Remove(draftUniqueId);
}
