namespace Wino.Platform.Apple;

/// <summary>
/// The app's bundle identifier, supplied by the app head (com.winomail.macos for both the Mac and the iOS app).
/// It names the Keychain services, the Application Support folder and the temporary folder, so a
/// head must never change its value for an existing app.
/// </summary>
public sealed class AppleApplicationIdentity
{
    public AppleApplicationIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
