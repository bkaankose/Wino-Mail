"""Provision, seed, and verify the Stalwart 1.0 OAuth server of the Wino lab. Python standard library only.

Commands (run inside the `seed` compose service with ./tls mounted at /tls):
  provision  apply the server settings through the management API (idempotent). Writes
             /state/stalwart-oauth.restart when a listener changed and the container must restart.
  seed       append a few messages to frank@stalwartoauth.test (skips messages already present)
  verify     TLS capabilities, metadata, dynamic registration, the redirect URI check, a full
             authorization code + PKCE sign-in, IMAP/SMTP OAUTHBEARER and XOAUTH2, refresh rotation
"""
import base64
import datetime as dt
import email.policy
import email.utils
import hashlib
import imaplib
import json
import re
import secrets
import smtplib
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from email.message import EmailMessage
from pathlib import Path

PASSWORD = '1'
STATE = Path('/state')
TLS = Path('/tls')
HOST = 'mail.stalwartoauth.test'
DOMAIN = 'stalwartoauth.test'
USER = 'frank'
ADDRESS = f'{USER}@{DOMAIN}'
ISSUER = f'https://{HOST}'
ADMIN_API = 'http://stalwart-oauth:8080'
MAIL_SCOPE = 'urn:ietf:params:oauth:scope:mail'
RESTART_MARKER = STATE / 'stalwart-oauth.restart'
LOOPBACK_REDIRECT = 'http://127.0.0.1/callback'


def log(message):
    print(message, flush=True)


def tls_context():
    context = ssl.create_default_context(cafile=str(TLS / 'ca.crt'))
    return context


def request(method, url, body=None, headers=None, auth=None, context=None):
    req = urllib.request.Request(url, data=body, method=method, headers=dict(headers or {}))
    if auth:
        req.add_header('Authorization', 'Basic ' + base64.b64encode(f'{auth[0]}:{auth[1]}'.encode()).decode())
    handler = urllib.request.HTTPSHandler(context=context or tls_context())
    opener = urllib.request.build_opener(handler, NoRedirect())
    try:
        with opener.open(req, timeout=20) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as error:
        return error.code, dict(error.headers), error.read()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def json_request(method, url, payload=None, form=None, auth=None):
    headers = {'Accept': 'application/json'}
    body = None
    if payload is not None:
        body = json.dumps(payload).encode()
        headers['Content-Type'] = 'application/json'
    elif form is not None:
        body = urllib.parse.urlencode(form).encode()
        headers['Content-Type'] = 'application/x-www-form-urlencoded'
    status, _, data = request(method, url, body, headers, auth)
    try:
        parsed = json.loads(data) if data else None
    except ValueError:
        parsed = {'raw': data.decode(errors='replace')[:400]}
    return status, parsed


# ----------------------------------------------------------------------------------------------
# Management API (JMAP with the urn:stalwart:jmap capability, as stalwart-cli uses it)
# ----------------------------------------------------------------------------------------------
def jmap(*calls):
    payload = {'using': ['urn:ietf:params:jmap:core', 'urn:stalwart:jmap'],
               'methodCalls': [[name, args, f'c{index}'] for index, (name, args) in enumerate(calls)]}
    status, data = json_request('POST', ADMIN_API + '/jmap', payload, auth=('admin', PASSWORD))
    if status != 200:
        raise RuntimeError(f'Stalwart management API: HTTP {status}: {data}')
    results = []
    for name, result, _ in data['methodResponses']:
        if name == 'error':
            raise RuntimeError(f'Stalwart management API error: {result}')
        for key in ('notCreated', 'notUpdated', 'notDestroyed'):
            if result.get(key):
                raise RuntimeError(f'Stalwart {name} {key}: {json.dumps(result[key])}')
        results.append(result)
    return results


def get_all(object_name):
    return jmap((f'{object_name}/get', {}))[0]['list']


def wait_api(seconds=180):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            status, _ = json_request('GET', ADMIN_API + '/jmap/session', auth=('admin', PASSWORD))
            if status == 200:
                return
            last = f'HTTP {status}'
        except OSError as error:
            last = str(error)
        time.sleep(2)
    raise RuntimeError(f'Stalwart OAuth management API not ready: {last}')


def provision():
    wait_api()
    restart = False
    certificate = (TLS / 'server.crt').read_text()
    private_key = (TLS / 'server.key').read_text()

    # The lab password "1" is below Stalwart's default policy (8 characters, zxcvbn three).
    jmap(('x:Authentication/set', {'update': {'singleton': {'minPasswordLength': 1, 'minPasswordStrength': 'zero'}}}))

    domain = next((item for item in get_all('x:Domain') if item.get('name') == DOMAIN), None)
    if domain:
        domain_id = domain['id']
    else:
        created = jmap(('x:Domain/set', {'create': {'d': {
            'name': DOMAIN, 'description': 'Wino lab (OAuth)',
            'certificateManagement': {'@type': 'Manual'}, 'dkimManagement': {'@type': 'Manual'},
            'dnsManagement': {'@type': 'Manual'}}}}))[0]
        domain_id = created['created']['d']['id']
        log(f'  stalwart-oauth: domain {DOMAIN} created')

    current = [item for item in get_all('x:Certificate')
               if (item.get('certificate') or {}).get('value', '').strip() == certificate.strip()]
    if current:
        certificate_id = current[0]['id']
    else:
        stale = [item['id'] for item in get_all('x:Certificate')]
        created = jmap(('x:Certificate/set', {'create': {'c': {
            'certificate': {'@type': 'Text', 'value': certificate},
            'privateKey': {'@type': 'Text', 'secret': private_key}}}}))[0]
        certificate_id = created['created']['c']['id']
        log('  stalwart-oauth: TLS certificate uploaded')
        restart = True

    settings = jmap(('x:SystemSettings/get', {'ids': ['singleton']}))[0]['list'][0]
    wanted = {'defaultHostname': HOST, 'defaultDomainId': domain_id, 'defaultCertificateId': certificate_id}
    patch = {key: value for key, value in wanted.items() if settings.get(key) != value}
    if patch:
        jmap(('x:SystemSettings/set', {'update': {'singleton': patch}}))
        log(f'  stalwart-oauth: system settings updated ({", ".join(sorted(patch))})')
        restart = True
    if not current and stale:
        jmap(('x:Certificate/set', {'destroy': stale}))

    listeners = {item['name']: item for item in get_all('x:NetworkListener')}
    if 'submission' not in listeners:
        jmap(('x:NetworkListener/set', {'create': {'l': {
            'name': 'submission', 'bind': {'[::]:587': True}, 'protocol': 'smtp',
            'useTls': True, 'implicitTls': False}}}))
        log('  stalwart-oauth: submission listener on 587 (STARTTLS) created')
        restart = True

    jmap(('x:Action/set', {'create': {'r': {'@type': 'ReloadSettings'}}}),
         ('x:Action/set', {'create': {'t': {'@type': 'ReloadTlsCertificates'}}}))

    account = next((item for item in get_all('x:Account')
                    if item.get('@type') == 'User' and item.get('name') == USER), None)
    if not account:
        jmap(('x:Account/set', {'create': {'u': {
            '@type': 'User', 'name': USER, 'domainId': domain_id, 'description': 'Frank Stalwart OAuth',
            'credentials': {'0': {'@type': 'Password', 'secret': PASSWORD}}}}}))
        log(f'  stalwart-oauth: account {ADDRESS} created')

    if restart:
        RESTART_MARKER.write_text('listeners, hostname, or certificate changed\n')
        log('  stalwart-oauth: restart required to apply listener, hostname, or certificate changes')
    log('  stalwart-oauth: settings ready')


# ----------------------------------------------------------------------------------------------
# Mail
# ----------------------------------------------------------------------------------------------
def imap_tls():
    return imaplib.IMAP4_SSL(HOST, 993, ssl_context=tls_context(), timeout=30)


def seed():
    now = dt.datetime.now(dt.timezone.utc)
    messages = []
    for index, (sender, subject, body) in enumerate([
        ('Wino Lab <lab@wino.lab>', 'Welcome to the Stalwart OAuth mailbox',
         'This mailbox signs in with OAuth (authorization code + PKCE) or the password 1.\n'),
        ('Carol Stalwart <carol@stalwart.test>', 'Generic OAuth checklist',
         'Discovery, dynamic registration, sign-in, refresh, revocation, and the rejected-token retry.\n'),
        ('Alice Dovecot <alice@dovecot.test>', 'Re: Generic OAuth checklist',
         'Refresh tokens rotate on every use; presenting an old one revokes the whole grant.\n'),
    ]):
        message = EmailMessage(policy=email.policy.SMTP)
        message['From'] = sender
        message['To'] = f'Frank Stalwart OAuth <{ADDRESS}>'
        message['Subject'] = subject
        message['Message-ID'] = f'<oauth.{index:04d}@wino.lab>'
        message['Date'] = email.utils.format_datetime(now - dt.timedelta(days=3 - index))
        if index == 2:
            message['In-Reply-To'] = '<oauth.0001@wino.lab>'
            message['References'] = '<oauth.0001@wino.lab>'
        message.set_content(body)
        messages.append(message)
    with imap_tls() as client:
        client.login(ADDRESS, PASSWORD)
        client.select('INBOX')
        # SEARCH HEADER does not match these Message-IDs on Stalwart, so read the headers directly.
        present = {}
        status, data = client.uid('FETCH', '1:*', '(UID BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])')
        for part in data if status == 'OK' else []:
            if isinstance(part, tuple):
                uid = re.search(rb'UID (\d+)', part[0])
                identifier = re.search(rb'<[^>]+>', part[1])
                if uid and identifier:
                    present.setdefault(identifier.group(0).decode(), []).append(uid.group(1).decode())
        duplicates = [uid for uids in present.values() for uid in uids[1:]]
        if duplicates:
            client.uid('STORE', ','.join(duplicates), '+FLAGS', r'(\Deleted)')
            client.expunge()
        added = 0
        for message in messages:
            if message['Message-ID'] in present:
                continue
            date = imaplib.Time2Internaldate(email.utils.parsedate_to_datetime(message['Date']).timestamp())
            client.append('INBOX', None, date, message.as_bytes())
            added += 1
        status, data = client.status('INBOX', '(MESSAGES)')
        log(f'  stalwart-oauth: {added} message(s) added; INBOX {data[0].decode()}')


# ----------------------------------------------------------------------------------------------
# Verification
# ----------------------------------------------------------------------------------------------
def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def oauthbearer(token, port):
    return f'n,a={ADDRESS},\x01host={HOST}\x01port={port}\x01auth=Bearer {token}\x01\x01'


def xoauth2(token):
    return f'user={ADDRESS}\x01auth=Bearer {token}\x01\x01'


def register(redirect_uris, scope=None):
    body = {'client_name': 'Wino Mail lab check', 'redirect_uris': redirect_uris,
            'token_endpoint_auth_method': 'none', 'grant_types': ['authorization_code', 'refresh_token'],
            'response_types': ['code']}
    if scope:
        body['scope'] = scope
    return json_request('POST', ISSUER + '/auth/register', body)


def sign_in(metadata, client_id, redirect_uri, scope):
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode()
    state = secrets.token_urlsafe(16)
    query = urllib.parse.urlencode({'response_type': 'code', 'client_id': client_id, 'redirect_uri': redirect_uri,
                                    'scope': scope, 'state': state, 'code_challenge': challenge,
                                    'code_challenge_method': 'S256', 'login_hint': ADDRESS})
    # The /login page is a static form; it posts these JSON bodies to /api/auth.
    status, data = json_request('POST', ISSUER + '/api/auth',
                                {'type': 'authCode', 'accountName': ADDRESS, 'accountSecret': PASSWORD, 'query': query})
    require(status == 200, f'/api/auth authCode: HTTP {status} {data}')
    if data.get('type') == 'consentRequired':
        status, data = json_request('POST', ISSUER + '/api/auth',
                                    {'type': 'authConsent', 'token': data['token'], 'query': query, 'consent': True})
        require(status == 200, f'/api/auth authConsent: HTTP {status} {data}')
    require(data.get('type') in ('redirect', 'authenticated'), f'Unexpected sign-in response {data}')
    location = urllib.parse.urlparse(data['location'])
    params = urllib.parse.parse_qs(location.query)
    require(f'{location.scheme}://{location.netloc}{location.path}' == redirect_uri, f'Redirected to {data["location"]}')
    require(params.get('state', [None])[0] == state, 'State mismatch')
    require(params.get('iss', [metadata['issuer']])[0] == metadata['issuer'], 'iss mismatch in authorization response')
    require('code' in params, f'No authorization code in {data["location"]}')
    code = params['code'][0]
    status, tokens = json_request('POST', metadata['token_endpoint'], form={
        'grant_type': 'authorization_code', 'code': code, 'redirect_uri': redirect_uri,
        'client_id': client_id, 'code_verifier': verifier})
    require(status == 200 and 'access_token' in tokens, f'Token exchange: HTTP {status} {tokens}')
    return tokens


def verify():
    results = {}
    log('Stalwart OAuth server:')
    with imap_tls() as client:
        caps = [cap.decode() if isinstance(cap, bytes) else cap for cap in client.capabilities]
        results['imapCapabilities'] = caps
        log(f'  IMAP 993 pre-auth: {" ".join(caps)}')
        for mechanism in ('AUTH=OAUTHBEARER', 'AUTH=XOAUTH2', 'AUTH=PLAIN'):
            require(mechanism in caps, f'IMAP over TLS does not advertise {mechanism}')
        client.login(ADDRESS, PASSWORD)
        log('  IMAP 993 password LOGIN: ok')
    for port in (465, 587):
        if port == 465:
            server = smtplib.SMTP_SSL(HOST, port, context=tls_context(), timeout=30)
        else:
            server = smtplib.SMTP(HOST, port, timeout=30)
            server.ehlo('verify.wino.lab')
            server.starttls(context=tls_context())
        with server:
            server.ehlo('verify.wino.lab')
            mechanisms = server.esmtp_features.get('auth', '').upper().split()
            results[f'smtp{port}Auth'] = mechanisms
            log(f'  SMTP {port} AUTH: {" ".join(mechanisms)}')
            for mechanism in ('OAUTHBEARER', 'XOAUTH2', 'PLAIN'):
                require(mechanism in mechanisms, f'SMTP {port} does not advertise {mechanism}')
            server.login(ADDRESS, PASSWORD)
        log(f'  SMTP {port} password AUTH: ok')

    status, metadata = json_request('GET', ISSUER + '/.well-known/oauth-authorization-server')
    require(status == 200, f'Metadata: HTTP {status}')
    require(metadata['issuer'] == ISSUER, f'Issuer is {metadata["issuer"]}, expected {ISSUER}')
    require('S256' in metadata.get('code_challenge_methods_supported', []), 'PKCE S256 missing')
    require(MAIL_SCOPE in metadata.get('scopes_supported', []), f'{MAIL_SCOPE} missing')
    require(metadata.get('registration_endpoint'), 'registration_endpoint missing')
    results['metadata'] = metadata
    log(f'  metadata: issuer {metadata["issuer"]}, registration {metadata["registration_endpoint"]}')
    status, oidc = json_request('GET', ISSUER + '/.well-known/openid-configuration')
    log(f'  openid-configuration: HTTP {status}')

    # Without a registered scope the client may only ask for openid/offline_access-free sign-in;
    # mail access needs the scope in the registration request (RFC 7591 "scope").
    status, bare = register([LOOPBACK_REDIRECT])
    require(status == 201, f'Dynamic registration without scope: HTTP {status} {bare}')
    for scope in (MAIL_SCOPE, f'{MAIL_SCOPE} offline_access'):
        try:
            sign_in(metadata, bare['client_id'], LOOPBACK_REDIRECT, scope)
            outcome = 'allowed'
        except RuntimeError as error:
            outcome = 'refused (' + str(error)[:120] + ')'
        results.setdefault('unscopedClient', {})[scope] = outcome
        log(f'  client registered without "scope", requesting "{scope}": {outcome}')
    status, client = register([LOOPBACK_REDIRECT, 'app.winomail.lab:/oauth'], f'{MAIL_SCOPE} offline_access')
    require(status == 201 and client.get('client_id'), f'Dynamic registration: HTTP {status} {client}')
    log(f'  dynamic registration (loopback + reverse-DNS scheme): HTTP 201, client_id {client["client_id"][:24]}...')
    status, rejected = register(['wino://auth-complete/oauth', LOOPBACK_REDIRECT])
    results['winoSchemeRegistration'] = {'status': status, 'body': rejected}
    log(f'  dynamic registration with wino://auth-complete/oauth: HTTP {status} {rejected.get("error") if rejected else ""}')

    # CVE-2026-81036: an unregistered redirect URI must be refused before any code is issued.
    query = urllib.parse.urlencode({'response_type': 'code', 'client_id': client['client_id'],
                                    'redirect_uri': 'http://127.0.0.1/evil', 'scope': MAIL_SCOPE,
                                    'code_challenge': 'x' * 43, 'code_challenge_method': 'S256'})
    status, data = json_request('POST', ISSUER + '/api/auth',
                                {'type': 'authCode', 'accountName': ADDRESS, 'accountSecret': PASSWORD, 'query': query})
    issued = isinstance(data, dict) and 'code=' in str(data.get('location', ''))
    results['unregisteredRedirect'] = {'status': status, 'body': data}
    require(not issued, f'Unregistered redirect URI accepted: {data}')
    log(f'  unregistered redirect URI refused: HTTP {status} {data.get("type") if isinstance(data, dict) else data}')

    tokens = sign_in(metadata, client['client_id'], LOOPBACK_REDIRECT, f'{MAIL_SCOPE} offline_access')
    results['tokenResponseKeys'] = sorted(tokens)
    log(f'  authorization code + PKCE: access token ({tokens.get("token_type")}, expires_in {tokens.get("expires_in")}), '
        f'refresh token {"present" if tokens.get("refresh_token") else "MISSING"}, scope "{tokens.get("scope")}"')
    access = tokens['access_token']

    with imap_tls() as imap_client:
        imap_client.authenticate('OAUTHBEARER', lambda _: oauthbearer(access, 993).encode())
        status, data = imap_client.select('INBOX', readonly=True)
        log(f'  IMAP AUTHENTICATE OAUTHBEARER: ok, INBOX has {data[0].decode()} message(s)')
    with imap_tls() as imap_client:
        imap_client.authenticate('XOAUTH2', lambda _: xoauth2(access).encode())
        log('  IMAP AUTHENTICATE XOAUTH2: ok')
    with smtplib.SMTP_SSL(HOST, 465, context=tls_context(), timeout=30) as server:
        server.ehlo('verify.wino.lab')
        server.auth('OAUTHBEARER', lambda challenge=None: oauthbearer(access, 465), initial_response_ok=True)
        log('  SMTP 465 AUTH OAUTHBEARER: ok')
    with imap_tls() as imap_client:
        try:
            imap_client.authenticate('OAUTHBEARER', lambda _: oauthbearer(access + 'x', 993).encode())
            raise RuntimeError('A tampered token was accepted')
        except imaplib.IMAP4.error as error:
            log(f'  IMAP OAUTHBEARER with a tampered token rejected: {error}')

    first_refresh = tokens.get('refresh_token')
    require(first_refresh, 'No refresh token issued with offline_access')
    status, refreshed = json_request('POST', metadata['token_endpoint'], form={
        'grant_type': 'refresh_token', 'refresh_token': first_refresh, 'client_id': client['client_id']})
    require(status == 200 and refreshed.get('access_token'), f'Refresh: HTTP {status} {refreshed}')
    rotated = refreshed.get('refresh_token') not in (None, first_refresh)
    log(f'  refresh: new access token, refresh token rotated={rotated}')
    status, reuse = json_request('POST', metadata['token_endpoint'], form={
        'grant_type': 'refresh_token', 'refresh_token': first_refresh, 'client_id': client['client_id']})
    # Stalwart accepts an immediate reuse (grace period); a reuse tens of seconds later returns
    # invalid_grant and revokes the whole grant, including the newest refresh token.
    log(f'  immediate reuse of the superseded refresh token: HTTP {status} {reuse.get("error") if isinstance(reuse, dict) else reuse}')
    if refreshed.get('refresh_token'):
        status, after = json_request('POST', metadata['token_endpoint'], form={
            'grant_type': 'refresh_token', 'refresh_token': refreshed['refresh_token'], 'client_id': client['client_id']})
        log(f'  newest refresh token after the reuse: HTTP {status} '
            f'{after.get("error") if isinstance(after, dict) and status != 200 else "still valid"}')
    results['refreshRotated'] = rotated
    (STATE / 'stalwart-oauth.json').write_text(json.dumps(results, indent=2))
    log('  stalwart-oauth: verification passed (details in .state/stalwart-oauth.json)')


if __name__ == '__main__':
    STATE.mkdir(parents=True, exist_ok=True)
    command = sys.argv[1] if len(sys.argv) > 1 else 'all'
    if command not in ('provision', 'seed', 'verify', 'all'):
        raise SystemExit(f'Unknown command {command}')
    if command in ('provision', 'all'):
        provision()
    if command in ('seed', 'all'):
        seed()
    if command in ('verify', 'all'):
        verify()
