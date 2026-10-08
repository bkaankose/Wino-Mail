# OAuth for IMAP providers: feasibility notes

Research snapshot from 2026-09-29 for the Gmail sign-in reliability work and a possible generic
OAuth path for IMAP/SMTP accounts. Everything marked *unverified* was not confirmed against a
primary source and needs a check before it drives a design decision.

## 1. What Wino has today

- Gmail: authorization code + PKCE, public client, system browser, `HttpListener` on
  `http://127.0.0.1:{port}/authorize/`, tokens in JSON files under the app data folder.
  This is the RFC 8252 shape and the only redirect style Google allows for "Desktop app" clients.
- Outlook: MSAL with the token cache extension.
- IMAP: password or app password only. MailKit already ships `SaslMechanismOAuth2` (XOAUTH2)
  and `SaslMechanismOAuthBearer` (OAUTHBEARER); the client side of OAuth for IMAP is a one-liner.
  The cost is provider onboarding and token lifecycle, not the protocol.
- New in this change: `IExternalBrowserAuthenticationPresenter` shows an in-app waiting dialog with
  cancel and copy-link while the browser flow runs. It is provider-neutral and can host any future
  browser-based OAuth flow.

## 2. How providers use OAuth tokens on IMAP/SMTP

| Provider | SASL mechanism | Client registration | Public client (PKCE, no secret) | Refresh token notes |
| --- | --- | --- | --- | --- |
| Google | XOAUTH2, scope `https://mail.google.com/` (restricted, needs verification) | Self-service | Yes, loopback only | Expires after 6 months unused, on password change, after 7 days while the consent screen is in Testing, cap of 100 live tokens per account/client |
| Microsoft 365 / Outlook.com | XOAUTH2, scopes `https://outlook.office.com/IMAP.AccessAsUser.All`, `SMTP.Send`, `offline_access` | Self-service (Entra app) | Yes, `http://localhost` (port ignored) or `127.0.0.1`; `[::1]` not accepted | 90-day lifetime for desktop public clients, rotated on every use. Basic SMTP AUTH is disabled by default for existing tenants at end of 2026 |
| Yahoo / AOL / AT&T | OAUTHBEARER preferred, XOAUTH2 accepted, scope `mail-w` | Partner approval required for the mail scope | Unverified | Issued; lifetime undocumented. Thunderbird uses a custom-scheme redirect |
| Fastmail | XOAUTH2 and OAUTHBEARER | Manual, via Fastmail partnerships | Yes, PKCE is mandatory; loopback, reverse-DNS scheme, or https redirect | Rotated on every refresh; RFC 8414 metadata published |
| Yandex | XOAUTH2, scopes `mail:imap_full`, `mail:smtp` | Self-service | Authorization yes, but the refresh call requires the client secret | Same TTL as the access token; Yandex advises refreshing every 3 months |
| Mail.ru | XOAUTH2, scope `mail.imap` | Self-service | No, client secret required, no PKCE | Standard refresh |
| Zoho | Unverified for IMAP; Zoho documents OAuth for its REST API only | Self-service | Unknown | Unknown |
| iCloud | None, app-specific passwords only | n/a | n/a | n/a |
| GMX / web.de | No developer documentation found; likely none | n/a | n/a | n/a |
| Proton | None; Bridge is a local IMAP server with a bridge password | n/a | n/a | n/a |

Sources: Google XOAUTH2 and native-app guides, Microsoft "Authenticate an IMAP, POP or SMTP
connection using OAuth" and refresh token docs, Yahoo sender developer docs, Fastmail OAuth page,
Yandex ID docs, Thunderbird `OAuth2Providers.sys.mjs`, MailKit `ExchangeOAuth2.md` and
`GMailOAuth2.md`.

Practical reading: a generic "OAuth for IMAP" feature realistically covers Microsoft accounts on
IMAP (useful for tenants that block the Graph app or for users who prefer IMAP), Google (already
handled by the Gmail provider), and Fastmail or Yahoo only after a partner registration. Yandex and
Mail.ru work only if Wino ships a client secret the way Thunderbird does, which is acceptable for
a desktop app (the secret is not confidential) but must be a deliberate choice.

## 3. Flows that fit a native Windows app

| Flow | Fit | Notes |
| --- | --- | --- |
| Authorization code + PKCE, loopback redirect (RFC 8252) | Best default | Google, Microsoft, Fastmail, Mail.ru accept loopback. Already implemented for Gmail. |
| Authorization code + PKCE, private-use scheme redirect | Secondary | Needed for Yahoo-style providers. Google rejects custom schemes for desktop clients. Requires a `uap:Protocol` entry in the manifest and instance redirection. |
| Device authorization grant (RFC 8628) | Microsoft only | Google's device flow does not allow Gmail scopes. Good fallback when no browser can be launched at all. |
| Discovery (RFC 8414 / OIDC) | Nice to have | Google, Microsoft, Fastmail publish metadata. Not enough coverage to replace a static table. |
| Embedded WebView2 | Avoid | Google returns `403 disallowed_useragent`; discouraged by RFC 8252. |

How other clients map a host to an issuer: Thunderbird desktop and Thunderbird for Android keep a
static hostname to issuer table with hardcoded client ids (and secrets where the provider demands
one). FairEmail does the same and lets advanced users import a custom providers file with their own
client id and secret. Evolution ships Microsoft app ids with a tenant override. None of them
discover OAuth capability from the IMAP `CAPABILITY` response alone; `AUTH=XOAUTH2` tells you the
server accepts a token, not where to get one.

## 4. Windows App SDK options for the browser hop

- `Windows.Security.Authentication.Web.WebAuthenticationBroker` is not supported in desktop or
  WinUI 3 apps (`COMException 0x800706BD`; Microsoft lists no alternative for it).
- `Microsoft.Security.Authentication.OAuth.OAuth2Manager` (Windows App SDK 1.7+): launches the
  default browser, expects a custom protocol redirect, completes the request from the second,
  protocol-activated process with `CompleteAuthRequest` and then terminates that process. PKCE S256
  by default, auth code flow only. The docs are inconsistent about whether it is in the stable
  channel as of 2026-08; verify against the shipped package. No loopback support, so it brings
  nothing for Google. Native AOT compatibility is unverified.
- WinUIEx `WebAuthenticator` (the "WindowEx" library): same protocol-activation design, needs
  `WebAuthenticator.CheckOAuthRedirectionActivation()` before any UI, launches the browser through
  `rundll32 url.dll,FileProtocolHandler`, relies on the identity provider echoing `state` untouched.
  Deprecated by its author since WinUIEx 2.6 in favor of `OAuth2Manager`. Not worth adopting new.
- MSAL with the WAM broker works for Microsoft identities only; its system-browser fallback is the
  same loopback listener Wino uses for Google.

Loopback hardening that matters more than any broker for the reported Google failures:

- Browser launch: `Launcher.LaunchUriAsync` returns `false` instead of throwing and needs the app in
  the foreground. Wino already resolves the default browser executable directly; the new dialog adds
  the copy-link escape hatch when both paths fail.
- `HttpListener` on `http://127.0.0.1:{port}` needs no URL ACL for the current user on Windows 10+,
  but group policy can remove the reservation. A raw `TcpListener` sidesteps HTTP.sys entirely if
  `HttpListenerException` (code 5) shows up in the field.
- Bind the literal used in `redirect_uri` (`127.0.0.1`, not `localhost`) so IPv6 resolution cannot
  split the listener and the browser. Use an OS-assigned port and avoid browser-unsafe ports.
- Add a wait timeout and log the failure class (launch, listener, redirect, exchange) so support
  reports become actionable.

## 5. Managing access and refresh tokens for IMAP providers

1. Storage: keep the refresh token per account in the Windows credential locker (`PasswordVault`)
   or DPAPI-protected storage rather than the plain JSON files the Gmail provider uses today. This
   is also the right moment to move the Gmail store.
2. Refresh policy: refresh before expiry (around 80 percent of `expires_in`, default one hour when
   absent) and always before the IMAP pool opens or resumes a connection. MailKit does not refresh
   tokens; an expired token surfaces as `AuthenticationException` on the next connect.
3. Rotation: persist a returned refresh token immediately (Microsoft, Fastmail, Yandex rotate) and
   only drop the previous one once the new one is written.
4. Single flight: one refresh per account at a time (`SemaphoreSlim`, as the Gmail provider already
   does) so pooled IMAP connections and SMTP never race. Google's 100 live token cap makes this
   mandatory.
5. Failure mapping: `invalid_grant` or a 400/401 on refresh sets `AccountAttentionReason`
   `InvalidCredentials` and routes through the existing fix-account sign-in path. Network errors do
   not; retry them.
6. Clock: compute expiry from `expires_in` against local time with a one to two minute skew margin;
   do not trust JWT `exp`.

## 6. Suggested shape for a generic implementation

- A provider table `ImapOAuthIssuer` keyed by IMAP and SMTP host names: authorize and token
  endpoints, scopes, SASL mechanism preference (OAUTHBEARER before XOAUTH2), redirect style
  (loopback or scheme), client id, optional client secret, PKCE flag, and an optional discovery URL.
  Start with Microsoft and Google; add Fastmail and Yahoo when a registration exists.
- A shared `BrowserAuthorizationCodeFlow` extracted from `WinoGmailCodeReceiver`: build the
  authorization URI, run the loopback listener, drive `IExternalBrowserAuthenticationPresenter`,
  exchange the code. The Gmail authenticator becomes one consumer of it.
- An `IImapOAuthTokenStore` behind the credential locker and an `ImapOAuthAuthenticator`
  implementing `IAuthenticator` so `SynchronizationManager.HandleAuthorizationAsync` and the
  fix-account flow work unchanged.
- In the IMAP synchronizer: if the account has an OAuth issuer, fetch a fresh token through the
  authenticator and authenticate with `SaslMechanismOAuthBearer` or `SaslMechanismOAuth2` depending
  on `client.AuthenticationMechanisms`; otherwise keep the password path.
- Wizard: when the entered IMAP host matches an issuer, offer "Sign in with {provider}" next to the
  password fields, and keep an advanced escape hatch for a user-supplied client id and secret
  (FairEmail model) so power users can use providers Wino has no registration for.
