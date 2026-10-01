"""Provision protocol fixtures using only Python's standard library."""
import base64
import datetime as dt
import email.policy
import email.utils
from email.message import EmailMessage
import imaplib
import json
import re
from pathlib import Path
import smtplib
import sys
import time
import urllib.error
import urllib.request
import uuid
from zoneinfo import ZoneInfo

PASSWORD = 'WinoLab123!'
USERS = ('alice', 'bob', 'empty')
STATE = Path('/state')
DAV = 'http://dav/dav.php/'
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
CATALOG = []


def save_json(path, value):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')
    temporary.replace(path)


def anchor():
    path = STATE / 'anchor.json'
    if not path.exists():
        # A date only; explicit local fixture times do not depend on the host zone.
        save_json(path, {'date': dt.datetime.now(dt.timezone.utc).astimezone(
            ZoneInfo('Europe/Warsaw')).date().isoformat()})
    return dt.date.fromisoformat(json.loads(path.read_text())['date'])


def dav(user, method, path, body=None, headers=None, allowed=(200, 201, 204, 207)):
    auth = base64.b64encode(f'{user}:{PASSWORD}'.encode()).decode()
    request = urllib.request.Request(DAV + path, data=body, method=method,
        headers={'Authorization': 'Basic ' + auth, **(headers or {})})
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            status, data = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, data = error.code, error.read()
    if status not in allowed:
        raise RuntimeError(f'DAV {method} {path}: {status}: {data.decode(errors="replace")[:700]}')
    return status, data


def imap(user):
    client = imaplib.IMAP4('mail', 143, timeout=15)
    client.login(f'{user}@wino.test', PASSWORD)
    return client


def wait_ready():
    deadline = time.monotonic() + 180
    last = None
    while time.monotonic() < deadline:
        try:
            for user in USERS:
                with imap(user):
                    pass
                dav(user, 'PROPFIND', f'principals/{user}/', headers={'Depth': '0'})
                with smtplib.SMTP('mail', 587, timeout=15) as smtp:
                    smtp.login(f'{user}@wino.test', PASSWORD)
            print('All three IMAP/DAV users and SMTP authentication ready', flush=True)
            return
        except (OSError, imaplib.IMAP4.error, smtplib.SMTPException, RuntimeError) as error:
            last = error
            time.sleep(3)
    raise RuntimeError(f'Protocol readiness timed out after 180 seconds: {last}')


def utf7(value):
    # IMAP modified UTF-7, independent of IMAP UTF8=ACCEPT support.
    result, pending = '', ''
    for char in value:
        if ' ' <= char <= '~':
            if pending:
                result += '&' + base64.b64encode(pending.encode('utf-16be')).decode().rstrip('=').replace('/', ',') + '-'
                pending = ''
            result += '&-' if char == '&' else char
        else:
            pending += char
    if pending:
        result += '&' + base64.b64encode(pending.encode('utf-16be')).decode().rstrip('=').replace('/', ',') + '-'
    return result


def message(user, key, subject, day, kind='plain'):
    msg = EmailMessage(policy=email.policy.SMTP)
    msg['From'] = 'Bob Lab <bob@wino.test>' if user == 'alice' else 'Alice Lab <alice@wino.test>'
    msg['To'] = f'{user.title()} Lab <{user}@wino.test>'
    msg['Subject'] = f'[Lab {key}] {subject}'
    msg['Message-ID'] = f'<{user}.{key}@wino.test>'
    msg['Date'] = email.utils.format_datetime(dt.datetime.combine(day, dt.time(10), dt.timezone.utc))
    msg.set_content(f'Fixture {key}: {subject}\nZażółć gęślą jaźń. 日本語. مرحبا.\n')
    if kind == 'html':
        msg.add_alternative('<html><body><h2>HTML fixture</h2><p>Bold <b>text</b> and a <a href="https://example.test">link</a>.</p><p dir="rtl">مرحبا بالعالم</p></body></html>', subtype='html')
    elif kind == 'inline':
        msg.add_alternative('<html><body>Inline image <img src="cid:lab-image"></body></html>', subtype='html')
        msg.get_payload()[-1].add_related(PNG, maintype='image', subtype='png', cid='<lab-image>', filename='inline.png')
    elif kind == 'attachments':
        for payload in (b'First attachment', b'Second attachment'):
            msg.add_attachment(payload, maintype='application', subtype='octet-stream', filename='duplicate.txt')
        msg.add_attachment('Unicode attachment: 日本語'.encode(), maintype='text', subtype='plain', filename='Zażółć-日本語.txt')
    elif kind == 'large':
        msg.add_attachment(b'Wino local lab.\n' * 327680, maintype='application', subtype='octet-stream', filename='large-5MiB.bin')
    return msg


MAIL_CASES = [
    ('unread', 'Unread plain text', 'INBOX', '', 'plain'),
    ('read', 'Read HTML alternative', 'INBOX', r'\Seen', 'html'),
    ('flagged', 'Flagged unread', 'INBOX', r'\Flagged', 'plain'),
    ('flagged-read', 'Flagged and read', 'INBOX', r'\Flagged \Seen', 'plain'),
    ('answered', 'Answered message', 'INBOX', r'\Answered \Seen', 'plain'),
    ('all-flags', 'Answered flagged read', 'INBOX', r'\Answered \Flagged \Seen', 'plain'),
    ('deleted', 'Deleted but not expunged', 'Trash', r'\Deleted \Seen', 'plain'),
    ('draft', 'Draft with recipients', 'Drafts', r'\Draft', 'html'),
    ('sent', 'Sent message', 'Sent', r'\Seen', 'plain'),
    ('junk', 'Junk example', 'Junk', '', 'plain'),
    ('archived', 'Archived message', 'Archive', r'\Seen', 'plain'),
    ('nested', 'Nested project message', 'Projects.Release', '', 'plain'),
    ('unicode', 'Unicode / RTL 日本語 مرحبا', 'Zażółć-日本語', '', 'html'),
    ('inline', 'Inline image', 'INBOX', '', 'inline'),
    ('attachments', 'Multiple and duplicate attachment names', 'INBOX', '', 'attachments'),
    ('large', 'Large attachment', 'INBOX', '', 'large'),
    ('high', 'High priority', 'INBOX', '', 'plain'),
    ('low', 'Low priority', 'INBOX', r'\Seen', 'plain'),
    ('thread-1', 'Thread root', 'INBOX', r'\Seen', 'plain'),
    ('thread-2', 'Thread reply', 'INBOX', '', 'plain'),
    ('thread-3', 'Thread follow-up', 'Sent', r'\Seen', 'plain'),
    ('invite', 'Meeting invitation REQUEST', 'INBOX', '', 'plain'),
    ('update', 'Meeting update REQUEST', 'INBOX', '', 'plain'),
    ('cancel', 'Meeting cancellation CANCEL', 'INBOX', '', 'plain'),
    ('rsvp', 'Meeting accepted REPLY', 'INBOX', '', 'plain'),
]


def seed_mail(day):
    ledger_path = STATE / 'mail-ledger.json'
    ledger = set(json.loads(ledger_path.read_text()) if ledger_path.exists() else [])
    for user in USERS:
        with imap(user) as client:
            for folder in ('Sent', 'Drafts', 'Trash', 'Junk', 'Archive', 'Projects', 'Projects.Release', 'Empty', 'Zażółć-日本語'):
                # CREATE reports NO for an existing folder; SELECT distinguishes that from failure.
                name = '"' + utf7(folder) + '"'
                status, data = client.create(name)
                if status != 'OK' and client.select(name, readonly=True)[0] != 'OK':
                    raise RuntimeError(f'Cannot create/select {folder}: {data}')
                client.subscribe(name)
            if user == 'empty':
                continue
            # Find message IDs across all baseline folders, so an interrupted seed can resume.
            existing = set()
            for folder in ('INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archive', 'Projects.Release', 'Zażółć-日本語'):
                client.select('"' + utf7(folder) + '"', readonly=True)
                status, ids = client.search(None, 'ALL')
                if status != 'OK':
                    raise RuntimeError('IMAP search failed')
                for number in ids[0].split():
                    status, data = client.fetch(number, '(BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])')
                    if status != 'OK':
                        raise RuntimeError('IMAP header fetch failed')
                    existing.update(part[1].decode().strip().split(': ', 1)[-1] for part in data if isinstance(part, tuple))
            for key, subject, folder, flags, kind in MAIL_CASES:
                identifier = f'{user}.{key}'
                CATALOG.append({'type': 'mail', 'user': user, 'id': identifier, 'name': subject, 'folder': folder, 'flags': flags})
                if identifier in ledger:
                    continue
                msg = message(user, key, subject, day, kind)
                if key in ('draft', 'sent', 'thread-3'):
                    msg.replace_header('From', f'{user.title()} Lab <{user}@wino.test>')
                    msg.replace_header('To', 'Bob Lab <bob@wino.test>' if user == 'alice' else 'Alice Lab <alice@wino.test>')
                    msg['Cc'] = 'Observer <observer@example.test>'
                    if key == 'draft':
                        msg['Bcc'] = 'Hidden <hidden@example.test>'
                if key in ('high', 'low'):
                    msg['X-Priority'] = '1' if key == 'high' else '5'
                    msg['Importance'] = 'high' if key == 'high' else 'low'
                if key.startswith('thread-'):
                    msg.replace_header('Subject', 'Lab conversation' if key == 'thread-1' else 'Re: Lab conversation')
                    if key != 'thread-1':
                        msg['In-Reply-To'] = f'<{user}.thread-1@wino.test>'
                        msg['References'] = f'<{user}.thread-1@wino.test>'
                if key in ('invite', 'update', 'cancel', 'rsvp'):
                    method = {'cancel': 'CANCEL', 'rsvp': 'REPLY'}.get(key, 'REQUEST')
                    organizer = user if key == 'rsvp' else ('bob' if user == 'alice' else 'alice')
                    content = calendar(day, user + '-mail-meeting', 'Lab invitation', meeting_properties(organizer), method=method,
                        extra=['SEQUENCE:' + ('1' if key in ('update', 'cancel') else '0')] + (['STATUS:CANCELLED'] if key == 'cancel' else []))
                    msg.add_attachment(content.encode(), maintype='text', subtype='calendar',
                        params={'method': method, 'charset': 'utf-8'}, filename=key + '.ics')
                if f'<{identifier}@wino.test>' not in existing:
                    status, data = client.append('"' + utf7(folder) + '"', '(' + flags + ')',
                        imaplib.Time2Internaldate(dt.datetime.combine(day, dt.time(10), dt.timezone.utc)), msg.as_bytes())
                    if status != 'OK':
                        raise RuntimeError(f'IMAP APPEND {identifier}: {data}')
                ledger.add(identifier)
                save_json(ledger_path, sorted(ledger))
    print(f'Mail fixtures ready ({len(ledger)} seeded baseline messages)', flush=True)


def stamp(day, hour=10, utc=True):
    return day.strftime('%Y%m%d') + f'T{hour:02d}0000' + ('Z' if utc else '')


def fold(lines):
    result = []
    for line in lines:
        current = ''
        for char in line:
            if len((current + char).encode()) > 73:
                result.append(current)
                current = ' '
            current += char
        result.append(current)
    return '\r\n'.join(result) + '\r\n'


TIMEZONE = ['BEGIN:VTIMEZONE', 'TZID:Europe/Warsaw', 'BEGIN:DAYLIGHT', 'DTSTART:19700329T020000',
    'TZOFFSETFROM:+0100', 'TZOFFSETTO:+0200', 'RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU', 'END:DAYLIGHT',
    'BEGIN:STANDARD', 'DTSTART:19701025T030000', 'TZOFFSETFROM:+0200', 'TZOFFSETTO:+0100',
    'RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU', 'END:STANDARD', 'END:VTIMEZONE']


def meeting_properties(user):
    other = 'bob' if user == 'alice' else 'alice'
    return [f'ORGANIZER;SCHEDULE-AGENT=CLIENT;CN={user.title()} Lab:mailto:{user}@wino.test',
        f'ATTENDEE;SCHEDULE-AGENT=CLIENT;CN=Accepted;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:{other}@wino.test',
        'ATTENDEE;SCHEDULE-AGENT=CLIENT;CN=Declined;ROLE=REQ-PARTICIPANT;PARTSTAT=DECLINED:mailto:declined@example.test',
        'ATTENDEE;SCHEDULE-AGENT=CLIENT;CN=Tentative;ROLE=OPT-PARTICIPANT;PARTSTAT=TENTATIVE:mailto:tentative@example.test',
        'ATTENDEE;SCHEDULE-AGENT=CLIENT;CN=Awaiting reply;RSVP=TRUE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:empty@wino.test']


def calendar(day, key, title, properties=(), start=None, end=None, extra=(), overrides=(), method=None):
    lines = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//Wino//Local Lab//EN', 'CALSCALE:GREGORIAN']
    if method:
        lines.append('METHOD:' + method)
    lines += TIMEZONE
    lines += ['BEGIN:VEVENT', 'UID:' + key + '@wino.test', 'DTSTAMP:' + stamp(day), 'SUMMARY:[Lab] ' + title,
        start or 'DTSTART:' + stamp(day + dt.timedelta(days=1)),
        end or 'DTEND:' + stamp(day + dt.timedelta(days=1), 11)] + list(properties) + list(extra) + ['END:VEVENT']
    for override in overrides:
        lines += ['BEGIN:VEVENT', 'UID:' + key + '@wino.test', 'DTSTAMP:' + stamp(day)] + override + ['END:VEVENT']
    return fold(lines + ['END:VCALENDAR'])


def events(day, user):
    tomorrow = day + dt.timedelta(days=1)
    cases = []
    def add(key, title, **kwargs):
        cases.append((key, title, calendar(day, user + '-' + key, title, **kwargs)))
    add('meeting', 'Attendees, reminder and online meeting', properties=meeting_properties(user) + [
        'LOCATION:Warsaw office', 'DESCRIPTION:Agenda line one\\nAgenda line two\\, with punctuation.',
        'URL:https://meet.example.test/wino', 'CLASS:PRIVATE', 'TRANSP:OPAQUE', 'STATUS:CONFIRMED',
        'BEGIN:VALARM', 'ACTION:DISPLAY', 'TRIGGER:-PT15M', 'DESCRIPTION:Meeting reminder', 'END:VALARM'])
    add('past', 'Past UTC event', start='DTSTART:' + stamp(day - dt.timedelta(days=7)), end='DTEND:' + stamp(day - dt.timedelta(days=7), 11))
    add('today', 'Today UTC event', start='DTSTART:' + stamp(day), end='DTEND:' + stamp(day, 11))
    add('future', 'Future event', start='DTSTART:' + stamp(day + dt.timedelta(days=60)), end='DTEND:' + stamp(day + dt.timedelta(days=60), 11))
    add('all-day', 'All-day event', start='DTSTART;VALUE=DATE:' + tomorrow.strftime('%Y%m%d'), end='DTEND;VALUE=DATE:' + (tomorrow + dt.timedelta(days=1)).strftime('%Y%m%d'))
    add('multiday', 'Three-day event (exclusive end)', start='DTSTART;VALUE=DATE:' + tomorrow.strftime('%Y%m%d'), end='DTEND;VALUE=DATE:' + (tomorrow + dt.timedelta(days=3)).strftime('%Y%m%d'))
    add('overlap', 'Overlapping public free event', properties=['CLASS:PUBLIC', 'TRANSP:TRANSPARENT', 'STATUS:TENTATIVE'])
    add('floating', 'Floating local time', start='DTSTART:' + stamp(tomorrow, utc=False), end='DTEND:' + stamp(tomorrow, 11, False))
    add('zoned', 'Warsaw zoned time', start='DTSTART;TZID=Europe/Warsaw:' + stamp(tomorrow, utc=False), end='DTEND;TZID=Europe/Warsaw:' + stamp(tomorrow, 11, False))
    for freq, rule in [('daily', 'FREQ=DAILY;COUNT=10'), ('weekly', 'FREQ=WEEKLY;BYDAY=MO,WE,FR;UNTIL=' + stamp(day + dt.timedelta(days=90))),
            ('monthly', 'FREQ=MONTHLY;BYDAY=1MO;COUNT=12'), ('yearly', 'FREQ=YEARLY;COUNT=3')]:
        add(freq, freq.title() + ' recurrence', properties=['RRULE:' + rule])
    add('exceptions', 'Daily with excluded and additional dates', properties=['RRULE:FREQ=DAILY;COUNT=8',
        'EXDATE:' + stamp(day + dt.timedelta(days=3)), 'RDATE:' + stamp(day + dt.timedelta(days=20))])
    add('overrides', 'Moved and cancelled recurring occurrences', properties=['RRULE:FREQ=DAILY;COUNT=7'], overrides=[
        ['RECURRENCE-ID:' + stamp(day + dt.timedelta(days=2)), 'DTSTART:' + stamp(day + dt.timedelta(days=2), 14),
         'DTEND:' + stamp(day + dt.timedelta(days=2), 15), 'SUMMARY:[Lab] Moved occurrence'],
        ['RECURRENCE-ID:' + stamp(day + dt.timedelta(days=3)), 'DTSTART:' + stamp(day + dt.timedelta(days=3)),
         'DTEND:' + stamp(day + dt.timedelta(days=3), 11), 'STATUS:CANCELLED', 'SUMMARY:[Lab] Cancelled occurrence']])
    add('old-series', 'Series beginning two years ago', start='DTSTART:' + stamp(day - dt.timedelta(days=730)),
        end='DTEND:' + stamp(day - dt.timedelta(days=730), 11), properties=['RRULE:FREQ=WEEKLY'])
    for month, key in [(3, 'spring-dst'), (10, 'autumn-dst')]:
        last = dt.date(day.year, month + 1, 1) - dt.timedelta(days=1)
        sunday = last - dt.timedelta(days=(last.weekday() + 1) % 7)
        add(key, 'Warsaw ' + key + ' recurrence', start='DTSTART;TZID=Europe/Warsaw:' + stamp(sunday - dt.timedelta(days=1), 9, False),
            end='DTEND;TZID=Europe/Warsaw:' + stamp(sunday - dt.timedelta(days=1), 10, False), properties=['RRULE:FREQ=DAILY;COUNT=4'])
    add('cancelled', 'Cancelled event', properties=['STATUS:CANCELLED'])
    return cases


def contacts(user):
    cases = []
    def add(key, title, version='3.0', properties=()):
        body = fold(['BEGIN:VCARD', 'VERSION:' + version, 'UID:' + contact_uid(user, key), 'FN:' + title,
            'N:' + title + ';;;;'] + list(properties) + ['END:VCARD'])
        cases.append((key, title, body))
    add('minimal', 'Lab Minimal')
    add('rich', 'Lab Rich Contact', properties=['EMAIL;TYPE=WORK:rich@wino.test', 'EMAIL;TYPE=HOME:rich@example.test',
        'TEL;TYPE=CELL:+48123456789', 'TEL;TYPE=WORK:+48221234567', 'ADR;TYPE=HOME:;;Example Street 1;Warsaw;;00-001;Poland',
        'ADR;TYPE=WORK:;;Business Street 2;Berlin;;10115;Germany', 'ORG:Wino Lab;Engineering', 'TITLE:Developer',
        'BDAY:19900115', 'NOTE:Line one\\nLine two\\, punctuation.', 'URL:https://example.test/rich',
        'PHOTO;ENCODING=b;TYPE=PNG:' + base64.b64encode(PNG).decode()])
    add('unicode', 'Lab Zażółć 日本語 مرحبا', '4.0', ['EMAIL:unicode@wino.test', 'TEL;VALUE=uri;TYPE=cell:tel:+48111222333', 'BDAY:--1015'])
    add('duplicate-a', 'Lab Same Name', properties=['EMAIL:shared@example.test'])
    add('duplicate-b', 'Lab Same Name', '4.0', ['EMAIL:shared@example.test'])
    add('group', 'Lab Team', '4.0', ['KIND:group', 'MEMBER:' + contact_uid(user, 'minimal'), 'MEMBER:' + contact_uid(user, 'rich')])
    return cases


def contact_uid(user, key):
    return 'urn:uuid:' + str(uuid.uuid5(uuid.NAMESPACE_URL, f'https://wino.test/{user}/{key}'))


def contact_collection(index, key):
    return 'contacts' if index < 4 or key == 'group' else 'team'


def seed_dav(day):
    for user in USERS:
        calendars = ('personal',) if user == 'empty' else ('personal', 'work')
        books = ('contacts',) if user == 'empty' else ('contacts', 'team')
        for collection in calendars:
            path = f'calendars/{user}/{collection}/'
            body = f'<c:mkcalendar xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:set><d:prop><d:displayname>Lab {user.title()} {collection.title()}</d:displayname><c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set></d:prop></d:set></c:mkcalendar>'.encode()
            if dav(user, 'PROPFIND', path, headers={'Depth': '0'}, allowed=(207, 404))[0] == 404:
                dav(user, 'MKCALENDAR', path, body, {'Content-Type': 'application/xml'})
            if user != 'empty':
                for index, (key, title, content) in enumerate(events(day, user)):
                    if (index % 2 == 0) != (collection == 'personal'):
                        continue
                    dav(user, 'PUT', path + key + '.ics', content.encode(), {'Content-Type': 'text/calendar; charset=utf-8', 'If-None-Match': '*'}, allowed=(201, 204, 412))
                    CATALOG.append({'type': 'event', 'user': user, 'id': key, 'name': title, 'collection': collection})
        for collection in books:
            path = f'addressbooks/{user}/{collection}/'
            body = f'<d:mkcol xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:set><d:prop><d:resourcetype><d:collection/><c:addressbook/></d:resourcetype><d:displayname>Lab {user.title()} {collection.title()}</d:displayname></d:prop></d:set></d:mkcol>'.encode()
            if dav(user, 'PROPFIND', path, headers={'Depth': '0'}, allowed=(207, 404))[0] == 404:
                dav(user, 'MKCOL', path, body, {'Content-Type': 'application/xml'})
            if user != 'empty':
                for index, (key, title, content) in enumerate(contacts(user)):
                    if contact_collection(index, key) != collection:
                        continue
                    dav(user, 'PUT', path + key + '.vcf', content.encode(), {'Content-Type': 'text/vcard; charset=utf-8', 'If-None-Match': '*'}, allowed=(201, 204, 412))
                    CATALOG.append({'type': 'contact', 'user': user, 'id': key, 'name': title, 'collection': collection})
    print('DAV collections, events and contacts ready', flush=True)


def verify():
    """Explicit setup check, not a Wino test suite. Intended for an unmodified baseline."""
    wait_ready()
    for user in USERS:
        with imap(user) as client:
            for key, title, folder, flags, kind in (() if user == 'empty' else MAIL_CASES):
                client.select('"' + utf7(folder) + '"', readonly=True)
                status, ids = client.search(None, 'HEADER', 'Message-ID', f'<{user}.{key}@wino.test>')
                if status != 'OK' or len(ids[0].split()) != 1:
                    raise RuntimeError(f'Missing/duplicate mail {user}.{key}')
                _, data = client.fetch(ids[0], '(FLAGS)')
                actual = data[0].decode()
                actual_flags = set(re.search(r'FLAGS \((.*?)\)', actual).group(1).split()) - {r'\Recent'}
                if actual_flags != set(flags.split()):
                    raise RuntimeError(f'Incorrect flags for {key}: {actual}')
            if user == 'empty':
                client.select('INBOX', readonly=True)
                if client.search(None, 'ALL')[1][0]:
                    raise RuntimeError('Empty inbox has messages')
        for key, title, content in (() if user == 'empty' else events(anchor(), user)):
            index = [case[0] for case in events(anchor(), user)].index(key)
            collection = 'personal' if index % 2 == 0 else 'work'
            _, data = dav(user, 'GET', f'calendars/{user}/{collection}/{key}.ics')
            if b'BEGIN:VEVENT' not in data:
                raise RuntimeError('Invalid event response')
        for index, (key, title, content) in enumerate(() if user == 'empty' else contacts(user)):
            collection = contact_collection(index, key)
            _, data = dav(user, 'GET', f'addressbooks/{user}/{collection}/{key}.vcf')
            if b'BEGIN:VCARD' not in data:
                raise RuntimeError('Invalid contact response')
    # Deliver a real SMTP message, observe it through IMAP, then remove only that probe.
    probe = message('bob', 'smtp-probe-' + str(time.time_ns()), 'SMTP delivery probe', anchor())
    with smtplib.SMTP('mail', 587, timeout=15) as smtp:
        smtp.login('alice@wino.test', PASSWORD)
        smtp.send_message(probe, from_addr='alice@wino.test', to_addrs=['bob@wino.test'])
    with imap('bob') as client:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            client.select('INBOX')
            _, ids = client.uid('SEARCH', None, 'HEADER', 'Message-ID', probe['Message-ID'])
            if ids[0]:
                client.uid('STORE', ids[0], '+FLAGS', r'(\Deleted)')
                if client.uid('EXPUNGE', ids[0])[0] != 'OK':
                    raise RuntimeError('Could not delete the SMTP probe with UID EXPUNGE')
                print('Verified IMAP fixtures/flags, DAV resources and Alice → Bob SMTP delivery', flush=True)
                return
            time.sleep(1)
    raise RuntimeError('SMTP probe did not arrive in Bob inbox')


if __name__ == '__main__':
    STATE.mkdir(parents=True, exist_ok=True)
    command = sys.argv[1] if len(sys.argv) > 1 else 'seed'
    if command == 'verify':
        verify()
    elif command == 'ready':
        wait_ready()
    elif command == 'seed':
        wait_ready()
        day = anchor()
        seed_mail(day)
        seed_dav(day)
        save_json(STATE / 'fixtures.json', {'anchorDate': day.isoformat(), 'fixtures': CATALOG})
        print(f'Seed complete: {len(CATALOG)} fixtures, anchor {day}', flush=True)
    else:
        raise ValueError(f'Unknown seeder command {command}')
