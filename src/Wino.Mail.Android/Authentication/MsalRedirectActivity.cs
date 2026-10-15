using Android.App;
using Android.Content;
using Microsoft.Identity.Client;

namespace Wino.Mail.Android.Authentication;

/// <summary>
/// Receives the Outlook sign-in redirect from the browser and hands it to MSAL (<see cref="BrowserTabActivity"/>).
/// The intent filter matches <c>msauth://com.winomail.android/...</c>; the path is the signing certificate hash, which
/// differs between debug and release keys, so it is not part of the filter (the package host is unique already).
/// Keep the host in sync with ApplicationId (AndroidOutlookAuthenticationHost.GetRedirectUri builds the full URI).
/// </summary>
[Activity(Exported = true)]
[IntentFilter([Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "msauth",
    DataHost = "com.winomail.android")]
public sealed class MsalRedirectActivity : BrowserTabActivity
{
}
