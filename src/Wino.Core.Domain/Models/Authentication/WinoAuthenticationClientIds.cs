namespace Wino.Core.Domain.Models.Authentication;

/// <summary>
/// Every OAuth client id Wino signs in with for Outlook and Gmail mailboxes and for Wino Account sign-in, on all
/// platforms. Client ids for IMAP providers live in <c>known-imap-providers.json</c>, and Android's Firebase setup reads
/// <c>google-services.json</c>.
/// </summary>
/// <remarks>
/// The iOS notification extension compiles this file as a link because it references no Wino project, so it must stay
/// free of dependencies. A stored token refreshes with the client that issued it: changing a value here signs existing
/// accounts out.
/// </remarks>
public static class WinoAuthenticationClientIds
{
    /// <summary>Microsoft Entra apps for Outlook mailboxes.</summary>
    public static class Outlook
    {
        /// <summary>The verified Wino Mail app every new Outlook account records before its first sign-in.</summary>
        public const string Current = "497b333d-c689-4b27-8f59-7dace624111a";

        /// <summary>
        /// Windows accounts without a stored client id were created with this app and stay on it forever; never migrate
        /// them, and never delete the app: the API uses its secret for Store collections.
        /// </summary>
        public const string Legacy = "b19c2035-d740-49ff-b297-de6ec561b208";
    }

    /// <summary>Google Cloud project 973025879644.</summary>
    public static class Google
    {
        /// <summary>
        /// The UWP client: Gmail mailboxes on Windows and Android's loopback fallback, and every Google token stored before
        /// tokens recorded their client. Never change, rotate or repurpose it, and never use it for Wino Accounts.
        /// </summary>
        public const string DesktopMailbox = "973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn.apps.googleusercontent.com";

        /// <summary>
        /// The iOS client, shared by the iOS and Mac apps through their common bundle id: Gmail mailboxes and Wino Account
        /// sign-in in an ASWebAuthenticationSession. The API lists it in <c>Secrets:Google:NativeClientIds</c>.
        /// </summary>
        public const string Apple = "973025879644-qlc58h7f2gepqgu3ocvlr7mqvd3fff1c.apps.googleusercontent.com";

        /// <summary>
        /// The web client: Android's Credential Manager <c>serverClientId</c> for Wino Account sign-in, so the ID token's
        /// audience is the API's own client id. The Android OAuth clients are matched by package and certificate, never named.
        /// </summary>
        public const string AndroidServer = "973025879644-92h0mdbvgaiq0p6efdv16hpiop65er6a.apps.googleusercontent.com";
    }
}
