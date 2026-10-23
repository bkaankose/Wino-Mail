"""Provision, seed, and verify the five-server Mail-Cal lab. Python standard library only.

Commands (run inside the `seed` compose service):
  ready      wait until every IMAP, SMTP, and DAV endpoint accepts the lab credentials
  provision  create domains/accounts on servers that need an API call (Stalwart, James)
  seed       create folders, ~150 generated messages per account, calendars, and contacts
  verify     print each server's capabilities and deliver one probe between every account pair
  all        provision, ready, seed, verify (used by local-lab.ps1 up)
"""
import base64
import datetime as dt
import email.policy
import email.utils
import imaplib
import io
import json
import random
import re
import smtplib
import sys
import time
import urllib.error
import urllib.request
import uuid
import zipfile
from email.message import EmailMessage
from pathlib import Path
from zoneinfo import ZoneInfo

PASSWORD = '1'
STATE = Path('/state')
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
MESSAGES_PER_ACCOUNT = 150

# Host-side values describe how Mail-Cal on Windows reaches each server (127.0.0.1 published ports).
ACCOUNTS = [
    {'key': 'dovecot', 'user': 'alice', 'name': 'Alice Dovecot', 'domain': 'dovecot.test',
     'imap': ('dovecot', 143), 'smtp': ('dovecot', 587), 'smtp_auth': True,
     'dav': 'http://baikal/dav.php/', 'dav_user': 'alice', 'cal_home': 'calendars/alice/', 'card_home': 'addressbooks/alice/',
     'host': {'imapPort': 1143, 'smtpPort': 1587, 'smtpAuth': 'NormalPassword', 'davUrl': 'http://127.0.0.1:8800/dav.php/',
              'calendarHome': 'http://127.0.0.1:8800/dav.php/calendars/alice/', 'addressBookHome': 'http://127.0.0.1:8800/dav.php/addressbooks/alice/'},
     'expect': {'QRESYNC': True, 'CONDSTORE': True}, 'server': 'docker-mailserver 16.0.1 (Dovecot 2.4 + Postfix), stock capabilities'},
    {'key': 'condstore', 'user': 'bob', 'name': 'Bob Condstore', 'domain': 'condstore.test',
     'imap': ('condstore', 143), 'smtp': ('condstore', 587), 'smtp_auth': True,
     'dav': 'http://radicale-condstore:5232/', 'dav_user': 'bob', 'cal_home': 'bob/', 'card_home': 'bob/',
     'host': {'imapPort': 2143, 'smtpPort': 2587, 'smtpAuth': 'NormalPassword', 'davUrl': 'http://127.0.0.1:8802/',
              'calendarHome': 'http://127.0.0.1:8802/bob/', 'addressBookHome': 'http://127.0.0.1:8802/bob/'},
     'expect': {'QRESYNC': False, 'CONDSTORE': True}, 'server': 'docker-mailserver 16.0.1 (Dovecot 2.4 + Postfix), QRESYNC removed'},
    {'key': 'stalwart', 'user': 'carol', 'name': 'Carol Stalwart', 'domain': 'stalwart.test',
     'imap': ('stalwart', 143), 'smtp': ('stalwart', 587), 'smtp_auth': True,
     'dav': 'http://stalwart:8080/dav/', 'dav_user': 'carol@stalwart.test', 'cal_home': 'cal/carol%40stalwart.test/', 'card_home': 'card/carol%40stalwart.test/',
     'host': {'imapPort': 3143, 'smtpPort': 3587, 'smtpAuth': 'NormalPassword', 'davUrl': 'http://127.0.0.1:8803/dav/cal/',
              'cardDavUrl': 'http://127.0.0.1:8803/dav/card/',
              'calendarHome': 'http://127.0.0.1:8803/dav/cal/carol%40stalwart.test/', 'addressBookHome': 'http://127.0.0.1:8803/dav/card/carol%40stalwart.test/'},
     'expect': {'QRESYNC': True, 'CONDSTORE': True}, 'server': 'Stalwart v0.13 (IMAP4rev2, JMAP, built-in CalDAV/CardDAV)'},
    {'key': 'greenmail', 'user': 'dave', 'name': 'Dave Greenmail', 'domain': 'greenmail.test',
     'imap': ('greenmail', 3143), 'smtp': ('dovecot', 25), 'smtp_auth': False,
     'dav': 'http://radicale-greenmail:5232/', 'dav_user': 'dave', 'cal_home': 'dave/', 'card_home': 'dave/',
     'host': {'imapPort': 4143, 'smtpPort': 4025, 'smtpAuth': 'None', 'davUrl': 'http://127.0.0.1:8804/',
              'calendarHome': 'http://127.0.0.1:8804/dave/', 'addressBookHome': 'http://127.0.0.1:8804/dave/'},
     'expect': {'QRESYNC': False, 'CONDSTORE': False}, 'server': 'GreenMail 2.1.8 (UID-only IMAP; outgoing mail through the hub)'},
    {'key': 'james', 'user': 'eve', 'name': 'Eve James', 'domain': 'james.test',
     'imap': ('james', 143), 'smtp': ('james', 587), 'smtp_auth': True,
     'dav': 'http://radicale-james:5232/', 'dav_user': 'eve', 'cal_home': 'eve/', 'card_home': 'eve/',
     'host': {'imapPort': 5143, 'smtpPort': 5587, 'smtpAuth': 'NormalPassword', 'davUrl': 'http://127.0.0.1:8805/',
              'calendarHome': 'http://127.0.0.1:8805/eve/', 'addressBookHome': 'http://127.0.0.1:8805/eve/'},
     'expect': None, 'server': 'Apache James 3.9.1 (memory backend); capabilities probed', 'volatile': True},
]
BY_KEY = {account['key']: account for account in ACCOUNTS}

EXTERNAL_PEOPLE = [
    ('Nina Kowalska', 'nina.kowalska@example.test'), ('Omar Haddad', 'omar.haddad@example.test'),
    ('Yuki Tanaka', 'yuki.tanaka@example.test'), ('Priya Natarajan', 'priya@example.test'),
    ('Lars Brekke', 'lars.brekke@example.test'), ('Sofia Esposito', 'sofia@example.test'),
    ('Mateusz Zieliński', 'mateusz@example.test'), ('Grace Okafor', 'grace.okafor@example.test'),
]
NOTIFIERS = [
    ('GitHub', 'notifications@github.example.test'), ('Jira', 'jira@atlassian.example.test'),
    ('Azure DevOps', 'azuredevops@microsoft.example.test'), ('Sentry', 'noreply@sentry.example.test'),
    ('Calendly', 'no-reply@calendly.example.test'),
]
NEWSLETTERS = [
    ('.NET Weekly', 'newsletter@dotnetweekly.example.test'), ('Windows Dev Digest', 'digest@windev.example.test'),
    ('Mail Protocol News', 'news@mailproto.example.test'), ('Design Systems Daily', 'hello@dsdaily.example.test'),
]
TOPICS = [
    'Release planning for 2.1', 'Invoice INV-{n}', 'Lunch on Thursday?', 'Sync token regression', 'Draft of the quarterly report',
    'Customer feedback roundup', 'Onboarding checklist', 'Design review notes', 'Flaky IMAP IDLE test', 'Office move update',
    'Expense report {n}', 'Travel itinerary Warsaw', 'Translation freeze', 'Budget approval', 'Weekly status',
    'Contract renewal', 'Bug triage: attachments', 'Beta feedback batch {n}', 'Photo from the hike', 'Question about CalDAV discovery',
    'Security audit follow-up', 'Team offsite agenda', 'Password rotation reminder', 'Welcome to the team', 'Re-running the migration',
]
SENTENCES = [
    'I went through the latest build and the folder list now loads in under a second.',
    'Could you double-check the attachment names when they contain non-ASCII characters?',
    'The sync token was reset after the UIDVALIDITY change, which looks correct to me.',
    'Let us keep the meeting short and focus on the open bugs.',
    'Zażółć gęślą jaźń is still my favourite test string for encoding problems.',
    'The customer reported that replies lose the quoted text on the second reply.',
    'Thanks for the quick turnaround, the fix works on my machine.',
    'Please find the numbers attached; the CSV has one row per account.',
    'I will be out on Friday, so Thursday works better for me.',
    'The invitation below is tentative until the room is confirmed.',
    '日本語のメールも問題なく表示されるか確認してください。',
    'مرحبا، هل يمكنك مراجعة الملف المرفق قبل الاجتماع؟',
    'We should talk about CONDSTORE fallback when the server drops QRESYNC mid-session.',
    'Nothing urgent, just keeping you in the loop.',
    'The large attachment is a synthetic binary; it is only there to test streaming.',
    'Ping me if the calendar entry does not show up after the next sync.',
]
ATTACHMENT_NAMES = ['notes.txt', 'numbers.csv', 'settings.json', 'diagram.png', 'export.zip', 'report.pdf',
    'Zażółć-日本語.txt', 'duplicate.txt', 'readme (final).txt', 'données.csv']
TIMEZONE = ['BEGIN:VTIMEZONE', 'TZID:Europe/Warsaw', 'BEGIN:DAYLIGHT', 'DTSTART:19700329T020000',
    'TZOFFSETFROM:+0100', 'TZOFFSETTO:+0200', 'RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU', 'END:DAYLIGHT',
    'BEGIN:STANDARD', 'DTSTART:19701025T030000', 'TZOFFSETFROM:+0200', 'TZOFFSETTO:+0100',
    'RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU', 'END:STANDARD', 'END:VTIMEZONE']


# ----------------------------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------------------------
def log(message):
    print(message, flush=True)


def save_json(path, value):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')
    temporary.replace(path)


def anchor():
    path = STATE / 'anchor.json'
    if not path.exists():
        save_json(path, {'date': dt.datetime.now(dt.timezone.utc).astimezone(ZoneInfo('Europe/Warsaw')).date().isoformat()})
    return dt.date.fromisoformat(json.loads(path.read_text())['date'])


def address(account):
    return f"{account['user']}@{account['domain']}"


def display(account):
    return f"{account['name']} <{address(account)}>"


def http(method, url, body=None, headers=None, auth=None, allowed=(200, 201, 204, 207)):
    request = urllib.request.Request(url, data=body, method=method, headers=dict(headers or {}))
    if auth:
        request.add_header('Authorization', 'Basic ' + base64.b64encode(f'{auth[0]}:{auth[1]}'.encode()).decode())
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            status, data = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, data = error.code, error.read()
    if allowed is not None and status not in allowed:
        raise RuntimeError(f'{method} {url}: {status}: {data.decode(errors="replace")[:600]}')
    return status, data


def dav(account, method, path, body=None, headers=None, allowed=(200, 201, 204, 207)):
    return http(method, account['dav'] + path, body, headers, (account['dav_user'], PASSWORD), allowed)


def imap(account):
    host, port = account['imap']
    client = imaplib.IMAP4(host, port, timeout=30)
    client.login(address(account), PASSWORD)
    return client


def smtp(account):
    host, port = account['smtp']
    client = smtplib.SMTP(host, port, timeout=30)
    client.ehlo('seed.mailcal.lab')
    if account['smtp_auth']:
        client.login(address(account), PASSWORD)
    return client


def utf7(value):
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


def quote(name):
    return '"' + utf7(name).replace('\\', '\\\\').replace('"', '\\"') + '"'


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


def stamp(day, hour=10, utc=True, minute=0):
    return day.strftime('%Y%m%d') + f'T{hour:02d}{minute:02d}00' + ('Z' if utc else '')


# ----------------------------------------------------------------------------------------------
# Server discovery
# ----------------------------------------------------------------------------------------------
def capabilities(account):
    with imap(account) as client:
        status, data = client.capability()
        if status != 'OK':
            raise RuntimeError(f'CAPABILITY failed for {account["key"]}')
        return data[0].decode().split()


def list_response(client, pattern='*', extended=''):
    """Return (attributes, delimiter, name) triples from LIST."""
    status, data = client.list('""', pattern) if not extended else client._simple_command('LIST', extended, '""', pattern)
    if status != 'OK' and not extended:
        raise RuntimeError('LIST failed')
    if extended:
        status, data = client._untagged_response(status, data, 'LIST')
    entries = []
    for item in data or []:
        if isinstance(item, tuple):
            item = item[0] + b' ' + item[1]
        if not item:
            continue
        match = re.match(rb'\((?P<attrs>[^)]*)\)\s+(?:"(?P<delim>[^"]*)"|(?P<nildelim>NIL))\s+(?P<name>.*)', item)
        if not match:
            continue
        name = match.group('name').strip()
        if name.startswith(b'"') and name.endswith(b'"'):
            name = name[1:-1]
        delimiter = match.group('delim')
        entries.append((match.group('attrs').decode().split(), delimiter.decode() if delimiter else None, name.decode('ascii', 'replace')))
    return entries


def hierarchy(client):
    entries = list_response(client, '""')
    for attrs, delimiter, name in entries:
        if delimiter:
            return delimiter
    entries = list_response(client, '*')
    for attrs, delimiter, name in entries:
        if delimiter:
            return delimiter
    return '.'


def special_folders(client):
    """Map roles to server folder names advertised through SPECIAL-USE attributes."""
    roles = {}
    try:
        entries = list_response(client, '*', '(SPECIAL-USE)')
    except Exception:
        entries = list_response(client, '*')
    for attrs, delimiter, name in entries:
        lowered = [attr.lower() for attr in attrs]
        for role, attribute in (('sent', '\\sent'), ('drafts', '\\drafts'), ('trash', '\\trash'), ('junk', '\\junk'), ('archive', '\\archive')):
            if attribute in lowered and role not in roles:
                roles[role] = name
    return roles


def ensure_folder(client, name):
    status, data = client.create(quote(name))
    if status != 'OK' and client.select(quote(name), readonly=True)[0] != 'OK':
        raise RuntimeError(f'Cannot create or select folder {name}: {data}')
    try:
        client.subscribe(quote(name))
    except Exception:
        pass


def folder_layout(account, client):
    """Decide the real folder names for this server (special-use names win over defaults)."""
    separator = hierarchy(client)
    roles = special_folders(client)
    layout = {
        'inbox': 'INBOX',
        'sent': roles.get('sent', 'Sent'), 'drafts': roles.get('drafts', 'Drafts'), 'trash': roles.get('trash', 'Trash'),
        'junk': roles.get('junk', 'Junk'), 'archive': roles.get('archive', 'Archive'),
        'projects': 'Projects', 'release': 'Projects' + separator + 'Release',
        'newsletters': 'Newsletters', 'unicode': 'Zażółć-日本語', 'empty': 'Empty',
    }
    for role in ('sent', 'drafts', 'trash', 'junk', 'archive', 'projects', 'release', 'newsletters', 'empty'):
        ensure_folder(client, layout[role])
    try:
        ensure_folder(client, layout['unicode'])
    except Exception as error:
        log(f"  {account['key']}: Unicode folder not supported ({error}); using 'Unicode' instead")
        layout['unicode'] = 'Unicode'
        ensure_folder(client, layout['unicode'])
    return separator, layout


# ----------------------------------------------------------------------------------------------
# Mail generation
# ----------------------------------------------------------------------------------------------
class Generator:
    def __init__(self, account, day):
        self.account = account
        self.rng = random.Random(f'mailcal-lab/{account["key"]}')
        self.day = day
        self.counter = 0
        self.others = [a for a in ACCOUNTS if a['key'] != account['key']]

    def next_id(self):
        self.counter += 1
        return f"<{self.account['key']}.{self.counter:04d}@mailcal.lab>"

    def person(self, include_external=True):
        pool = [display(a) for a in self.others]
        if include_external:
            pool += [f'{name} <{mail}>' for name, mail in EXTERNAL_PEOPLE]
        return self.rng.choice(pool)

    def subject(self):
        return self.rng.choice(TOPICS).replace('{n}', str(self.rng.randint(100, 999)))

    def paragraphs(self, count=None):
        count = count or self.rng.randint(1, 4)
        return '\n\n'.join(' '.join(self.rng.sample(SENTENCES, self.rng.randint(1, 3))) for _ in range(count))

    def when(self, days_ago=None, hour=None):
        days_ago = self.rng.randint(0, 120) if days_ago is None else days_ago
        hour = self.rng.randint(7, 19) if hour is None else hour
        return dt.datetime.combine(self.day - dt.timedelta(days=days_ago), dt.time(hour, self.rng.randint(0, 59)), dt.timezone.utc)

    def base(self, sender, recipients, subject, when, html=None, cc=None, text=None):
        msg = EmailMessage(policy=email.policy.SMTP)
        msg['From'] = sender
        msg['To'] = ', '.join(recipients)
        if cc:
            msg['Cc'] = ', '.join(cc)
        msg['Subject'] = subject
        msg['Message-ID'] = self.next_id()
        msg['Date'] = email.utils.format_datetime(when)
        text = text or self.paragraphs()
        msg.set_content(text + '\n')
        if html is None:
            html = self.rng.random() < 0.6
        if html:
            body = ''.join(f'<p>{p}</p>' for p in text.split('\n\n'))
            msg.add_alternative(f'<html><body style="font-family:Segoe UI,sans-serif"><h3>{subject}</h3>{body}'
                f'<p>Regards,<br>{sender.split(" <")[0]}</p></body></html>', subtype='html')
        return msg

    def attachment_bytes(self, name):
        lower = name.lower()
        if lower.endswith('.csv'):
            rows = ['account,folder,count'] + [f'{a["key"]},INBOX,{self.rng.randint(10, 200)}' for a in ACCOUNTS]
            return '\n'.join(rows).encode(), 'text', 'csv'
        if lower.endswith('.json'):
            return json.dumps({'generated': True, 'seed': self.account['key'], 'values': [self.rng.random() for _ in range(5)]}, indent=2).encode(), 'application', 'json'
        if lower.endswith('.png'):
            return PNG, 'image', 'png'
        if lower.endswith('.zip'):
            buffer = io.BytesIO()
            with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED) as archive:
                archive.writestr('readme.txt', 'Generated by the Mail-Cal local lab.\n')
                archive.writestr('data/values.csv', 'a,b\n1,2\n')
            return buffer.getvalue(), 'application', 'zip'
        if lower.endswith('.pdf'):
            body = b'%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n' \
                b'3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n'
            return body, 'application', 'pdf'
        return (self.paragraphs(2) + '\n').encode(), 'text', 'plain'

    def attach(self, msg, count=None, large=False):
        names = self.rng.sample(ATTACHMENT_NAMES, count or self.rng.randint(1, 3))
        for name in names:
            data, maintype, subtype = self.attachment_bytes(name)
            msg.add_attachment(data, maintype=maintype, subtype=subtype, filename=name)
        if large:
            size = self.rng.randint(2, 5)
            msg.add_attachment(bytes(self.rng.getrandbits(8) for _ in range(1024)) * (size * 1024),
                maintype='application', subtype='octet-stream', filename=f'large-{size}MiB.bin')
        return names

    def inline(self, msg):
        html = msg.get_body(preferencelist=('html',))
        if html is None:
            if msg.is_multipart():
                return
            msg.add_alternative('<html><body>Inline image below<br><img src="cid:lab-image"></body></html>', subtype='html')
            html = msg.get_payload()[-1]
        html.add_related(PNG, maintype='image', subtype='png', cid='<lab-image>', filename='inline.png')

    def calendar(self, key, title, organizer, attendees, method, start, sequence=0, cancelled=False):
        lines = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//MailCal//Local Lab//EN', 'CALSCALE:GREGORIAN', 'METHOD:' + method] + TIMEZONE
        lines += ['BEGIN:VEVENT', 'UID:' + key + '@mailcal.lab', 'DTSTAMP:' + stamp(self.day), 'SUMMARY:' + title,
            'DTSTART;TZID=Europe/Warsaw:' + start, 'DTEND;TZID=Europe/Warsaw:' + start[:9] + f'{int(start[9:11]) + 1:02d}' + start[11:],
            'LOCATION:Warsaw office, room 4.2', 'DESCRIPTION:Generated invitation from the Mail-Cal local lab.',
            'SEQUENCE:' + str(sequence), f'ORGANIZER;CN={organizer[0]}:mailto:{organizer[1]}']
        for name, mail, partstat in attendees:
            lines.append(f'ATTENDEE;CN={name};ROLE=REQ-PARTICIPANT;PARTSTAT={partstat};RSVP=TRUE:mailto:{mail}')
        if cancelled:
            lines.append('STATUS:CANCELLED')
        lines += ['BEGIN:VALARM', 'ACTION:DISPLAY', 'TRIGGER:-PT15M', 'DESCRIPTION:Reminder', 'END:VALARM', 'END:VEVENT', 'END:VCALENDAR']
        return fold(lines)

    def generate(self, layout):
        """Return (msg, folder_role, flags, when) entries for this account."""
        me = self.account
        me_display = display(me)
        items = []

        def add(msg, role, flags, when):
            items.append((msg, role, flags, when))

        def seen(probability=0.65):
            return r'\Seen' if self.rng.random() < probability else ''

        # Conversations: 24 threads of 2-6 messages that involve this account.
        for thread in range(24):
            participants = self.rng.sample([display(a) for a in self.others] + [f'{n} <{m}>' for n, m in EXTERNAL_PEOPLE], self.rng.randint(1, 3))
            everyone = participants + [me_display]
            subject = self.subject()
            size = self.rng.randint(2, 6)
            when = self.when(days_ago=self.rng.randint(5, 115))
            root_id = None
            references = []
            previous_from_other = None
            for index in range(size):
                mine = self.rng.random() < 0.4 if index else self.rng.random() < 0.3
                sender = me_display if mine else self.rng.choice(participants)
                recipients = [p for p in everyone if p != sender]
                text = None
                if index and self.rng.random() < 0.5:
                    text = self.paragraphs(1) + f'\n\nOn {when:%a, %d %b %Y}, {sender.split(" <")[0]} wrote:\n> ' + self.paragraphs(1).replace('\n\n', '\n> ')
                msg = self.base(sender, recipients[:2], subject if index == 0 else 'Re: ' + subject, when, cc=recipients[2:] or None, text=text)
                if references:
                    msg['In-Reply-To'] = references[-1]
                    msg['References'] = ' '.join(references)
                if self.rng.random() < 0.2:
                    self.attach(msg)
                if mine:
                    add(msg, 'sent', r'\Seen', when)
                    if previous_from_other is not None:
                        previous_from_other[2] = (previous_from_other[2] + r' \Answered').strip()
                else:
                    entry = [msg, 'inbox', seen(0.7), when]
                    items.append(entry)
                    previous_from_other = entry
                    if self.rng.random() < 0.08:
                        entry[2] = (entry[2] + r' \Flagged').strip()
                references.append(msg['Message-ID'])
                root_id = root_id or msg['Message-ID']
                when += dt.timedelta(hours=self.rng.randint(1, 30), minutes=self.rng.randint(0, 59))

        # Notifications and personal single messages.
        for _ in range(30):
            if self.rng.random() < 0.4:
                name, mail = self.rng.choice(NOTIFIERS)
                subject = f'[{name}] ' + self.rng.choice(['Build #{n} failed', 'PR #{n} ready for review', 'New issue #{n} assigned to you',
                    'Deployment to staging finished', '{n} new events in the last hour']).replace('{n}', str(self.rng.randint(100, 9999)))
                msg = self.base(f'{name} <{mail}>', [me_display], subject, self.when(), html=True)
                msg['Auto-Submitted'] = 'auto-generated'
            else:
                msg = self.base(self.person(), [me_display], self.subject(), self.when())
                if self.rng.random() < 0.35:
                    self.attach(msg)
            if self.rng.random() < 0.12:
                self.inline(msg)
            if self.rng.random() < 0.15:
                msg['X-Priority'] = '1'
                msg['Importance'] = 'high'
            elif self.rng.random() < 0.1:
                msg['X-Priority'] = '5'
                msg['Importance'] = 'low'
            add(msg, 'inbox', (seen() + (r' \Flagged' if self.rng.random() < 0.1 else '')).strip(), msg['Date'])

        # Newsletters, junk, archive, trash, project, unicode, drafts.
        for _ in range(10):
            name, mail = self.rng.choice(NEWSLETTERS)
            msg = self.base(f'{name} <{mail}>', [me_display], f'{name}: issue #{self.rng.randint(40, 400)}', self.when(), html=True)
            msg['List-Unsubscribe'] = f'<mailto:unsubscribe@{mail.split("@")[1]}>'
            add(msg, 'newsletters', seen(0.5), msg['Date'])
        for _ in range(5):
            msg = self.base(f'Prize Desk <winner{self.rng.randint(1, 99)}@spam.example.test>', [me_display],
                self.rng.choice(['You have won!!!', 'URGENT: account verification', 'Cheap watches', 'Re: your package']), self.when(), html=True)
            add(msg, 'junk', '', msg['Date'])
        for _ in range(10):
            msg = self.base(self.person(), [me_display], self.subject(), self.when(days_ago=self.rng.randint(60, 120)))
            if self.rng.random() < 0.3:
                self.attach(msg)
            add(msg, 'archive', r'\Seen', msg['Date'])
        for _ in range(4):
            msg = self.base(self.person(), [me_display], self.subject(), self.when())
            add(msg, 'trash', r'\Seen' if self.rng.random() < 0.5 else '', msg['Date'])
        for _ in range(6):
            msg = self.base(self.person(False), [me_display], 'Release: ' + self.subject(), self.when())
            if self.rng.random() < 0.5:
                self.attach(msg, count=1)
            add(msg, 'release', seen(), msg['Date'])
        for _ in range(3):
            msg = self.base(self.person(), [me_display], self.rng.choice(['Zażółć gęślą jaźń', '日本語の件名', 'مرحبا بالعالم']), self.when(), html=True)
            add(msg, 'unicode', '', msg['Date'])
        for _ in range(3):
            msg = self.base(me_display, [self.person()], 'Draft: ' + self.subject(), self.when(days_ago=self.rng.randint(0, 3)))
            msg['Bcc'] = 'Hidden Copy <hidden@example.test>'
            add(msg, 'drafts', r'\Draft', msg['Date'])

        # Calendar invitations: requests from other lab accounts, an update, a cancellation, and replies.
        for index in range(8):
            organizer_account = self.rng.choice(self.others)
            organizer = (organizer_account['name'], address(organizer_account))
            attendees = [(me['name'], address(me), 'NEEDS-ACTION')] + [(a['name'], address(a), self.rng.choice(['ACCEPTED', 'TENTATIVE', 'NEEDS-ACTION']))
                for a in self.rng.sample(self.others, 2) if a['key'] != organizer_account['key']]
            key = f"{me['key']}-invite-{index}"
            start_day = self.day + dt.timedelta(days=self.rng.randint(-3, 21))
            start = stamp(start_day, self.rng.randint(9, 16), utc=False)
            title = self.rng.choice(['Sprint review', 'Design sync', 'Customer call', 'Release go/no-go', '1:1', 'Lunch & learn']) + f' ({index + 1})'
            if index < 5:
                method, subject, flags, role = 'REQUEST', 'Invitation: ' + title, '', 'inbox'
                sender, recipients = display(organizer_account), [me_display]
                content = self.calendar(key, title, organizer, attendees, method, start)
            elif index == 5:
                method, subject, flags, role = 'REQUEST', 'Updated invitation: ' + title, '', 'inbox'
                sender, recipients = display(organizer_account), [me_display]
                content = self.calendar(key, title, organizer, attendees, method, start, sequence=1)
            elif index == 6:
                method, subject, flags, role = 'CANCEL', 'Cancelled: ' + title, '', 'inbox'
                sender, recipients = display(organizer_account), [me_display]
                content = self.calendar(key, title, organizer, attendees, method, start, sequence=2, cancelled=True)
            else:
                method, subject, flags, role = 'REPLY', 'Accepted: ' + title, r'\Seen', 'inbox'
                replier = self.rng.choice(self.others)
                sender, recipients = display(replier), [me_display]
                content = self.calendar(key, title, (me['name'], address(me)), [(replier['name'], address(replier), 'ACCEPTED')], method, start)
            msg = self.base(sender, recipients, subject, self.when(days_ago=self.rng.randint(0, 10)), html=True)
            if index % 2 == 0:
                msg.add_alternative(content, subtype='calendar', params={'method': method, 'charset': 'utf-8'})
            else:
                msg.add_attachment(content.encode(), maintype='text', subtype='calendar',
                    params={'method': method, 'charset': 'utf-8'}, filename='invite.ics')
            add(msg, role, flags, msg['Date'])

        # One large attachment and one message with many attachments.
        msg = self.base(self.person(False), [me_display], 'Large attachment for streaming tests', self.when(days_ago=2))
        self.attach(msg, count=1, large=True)
        add(msg, 'inbox', '', msg['Date'])
        msg = self.base(self.person(False), [me_display], 'All attachment types in one message', self.when(days_ago=1), html=True)
        for name in ATTACHMENT_NAMES:
            data, maintype, subtype = self.attachment_bytes(name)
            msg.add_attachment(data, maintype=maintype, subtype=subtype, filename=name)
        self.inline(msg)
        add(msg, 'inbox', '', msg['Date'])

        # Top up with plain singles until the target count is reached.
        while len(items) < MESSAGES_PER_ACCOUNT:
            msg = self.base(self.person(), [me_display], self.subject(), self.when())
            add(msg, 'inbox', seen(), msg['Date'])

        result = []
        for msg, role, flags, when in items:
            if isinstance(when, str):
                when = email.utils.parsedate_to_datetime(when)
            result.append((msg, layout[role], flags, when))
        return result


# ----------------------------------------------------------------------------------------------
# Seeding
# ----------------------------------------------------------------------------------------------
def existing_message_ids(client, folders):
    found = set()
    for folder in folders:
        if client.select(quote(folder), readonly=True)[0] != 'OK':
            continue
        status, data = client.search(None, 'ALL')
        if status != 'OK' or not data[0]:
            continue
        status, data = client.fetch('1:*', '(BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])')
        if status != 'OK':
            continue
        for part in data:
            if isinstance(part, tuple):
                match = re.search(rb'<[^>]+>', part[1])
                if match:
                    found.add(match.group(0).decode())
    return found


def seed_mail(day, catalog):
    for account in ACCOUNTS:
        ledger_path = STATE / f"mail-ledger-{account['key']}.json"
        # The ledger keeps user deletions from being re-seeded. A server that forgets everything on
        # restart (James memory backend) must be re-seeded from what the server actually holds.
        ledger = set(json.loads(ledger_path.read_text()) if ledger_path.exists() and not account.get('volatile') else [])
        with imap(account) as client:
            separator, layout = folder_layout(account, client)
            messages = Generator(account, day).generate(layout)
            existing = existing_message_ids(client, sorted(set(layout.values())))
            appended = 0
            counts = {}
            for msg, folder, flags, when in messages:
                identifier = msg['Message-ID']
                counts[folder] = counts.get(folder, 0) + 1
                if identifier in ledger:
                    continue
                if identifier not in existing:
                    status, data = client.append(quote(folder), '(' + flags + ')' if flags else None,
                        imaplib.Time2Internaldate(when), msg.as_bytes())
                    if status != 'OK':
                        raise RuntimeError(f'IMAP APPEND {identifier} into {folder} on {account["key"]}: {data}')
                    appended += 1
                ledger.add(identifier)
                if appended % 25 == 0:
                    save_json(ledger_path, sorted(ledger))
            save_json(ledger_path, sorted(ledger))
            catalog[account['key']] = {'separator': separator, 'folders': layout, 'messageCount': len(messages), 'perFolder': counts}
            log(f"  {account['key']}: {len(messages)} messages ready ({appended} appended now), separator '{separator}'")


def meeting_properties(account):
    other = BY_KEY['dovecot'] if account['key'] != 'dovecot' else BY_KEY['condstore']
    return [f"ORGANIZER;SCHEDULE-AGENT=CLIENT;CN={account['name']}:mailto:{address(account)}",
        f"ATTENDEE;SCHEDULE-AGENT=CLIENT;CN={other['name']};ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:{address(other)}",
        'ATTENDEE;SCHEDULE-AGENT=CLIENT;CN=Declined;ROLE=REQ-PARTICIPANT;PARTSTAT=DECLINED:mailto:declined@example.test',
        'ATTENDEE;SCHEDULE-AGENT=CLIENT;CN=Tentative;ROLE=OPT-PARTICIPANT;PARTSTAT=TENTATIVE:mailto:tentative@example.test',
        'ATTENDEE;SCHEDULE-AGENT=CLIENT;CN=Awaiting reply;RSVP=TRUE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:nina.kowalska@example.test']


def event(day, key, title, properties=(), start=None, end=None, overrides=()):
    lines = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//MailCal//Local Lab//EN', 'CALSCALE:GREGORIAN'] + TIMEZONE
    lines += ['BEGIN:VEVENT', 'UID:' + key + '@mailcal.lab', 'DTSTAMP:' + stamp(day), 'SUMMARY:' + title,
        start or 'DTSTART:' + stamp(day + dt.timedelta(days=1)),
        end or 'DTEND:' + stamp(day + dt.timedelta(days=1), 11)] + list(properties) + ['END:VEVENT']
    for override in overrides:
        lines += ['BEGIN:VEVENT', 'UID:' + key + '@mailcal.lab', 'DTSTAMP:' + stamp(day)] + override + ['END:VEVENT']
    return fold(lines + ['END:VCALENDAR'])


def events(day, account):
    tomorrow = day + dt.timedelta(days=1)
    prefix = account['key'] + '-'
    cases = []

    def add(key, title, **kwargs):
        cases.append((key, title, event(day, prefix + key, title, **kwargs)))

    add('meeting', 'Team meeting with attendees', properties=meeting_properties(account) + [
        'LOCATION:Warsaw office', 'DESCRIPTION:Agenda line one\\nAgenda line two\\, with punctuation.',
        'URL:https://meet.example.test/mailcal', 'CLASS:PRIVATE', 'TRANSP:OPAQUE', 'STATUS:CONFIRMED',
        'BEGIN:VALARM', 'ACTION:DISPLAY', 'TRIGGER:-PT15M', 'DESCRIPTION:Meeting reminder', 'END:VALARM'])
    add('past', 'Past UTC event', start='DTSTART:' + stamp(day - dt.timedelta(days=7)), end='DTEND:' + stamp(day - dt.timedelta(days=7), 11))
    add('today', 'Today UTC event', start='DTSTART:' + stamp(day), end='DTEND:' + stamp(day, 11))
    add('future', 'Future event', start='DTSTART:' + stamp(day + dt.timedelta(days=60)), end='DTEND:' + stamp(day + dt.timedelta(days=60), 11))
    add('all-day', 'All-day event', start='DTSTART;VALUE=DATE:' + tomorrow.strftime('%Y%m%d'), end='DTEND;VALUE=DATE:' + (tomorrow + dt.timedelta(days=1)).strftime('%Y%m%d'))
    add('multiday', 'Three-day offsite', start='DTSTART;VALUE=DATE:' + tomorrow.strftime('%Y%m%d'), end='DTEND;VALUE=DATE:' + (tomorrow + dt.timedelta(days=3)).strftime('%Y%m%d'))
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
         'DTEND:' + stamp(day + dt.timedelta(days=2), 15), 'SUMMARY:Moved occurrence'],
        ['RECURRENCE-ID:' + stamp(day + dt.timedelta(days=3)), 'DTSTART:' + stamp(day + dt.timedelta(days=3)),
         'DTEND:' + stamp(day + dt.timedelta(days=3), 11), 'STATUS:CANCELLED', 'SUMMARY:Cancelled occurrence']])
    add('old-series', 'Weekly series from two years ago', start='DTSTART:' + stamp(day - dt.timedelta(days=730)),
        end='DTEND:' + stamp(day - dt.timedelta(days=730), 11), properties=['RRULE:FREQ=WEEKLY'])
    for month, key in [(3, 'spring-dst'), (10, 'autumn-dst')]:
        last = dt.date(day.year, month + 1, 1) - dt.timedelta(days=1)
        sunday = last - dt.timedelta(days=(last.weekday() + 1) % 7)
        add(key, 'Warsaw ' + key + ' recurrence', start='DTSTART;TZID=Europe/Warsaw:' + stamp(sunday - dt.timedelta(days=1), 9, False),
            end='DTEND;TZID=Europe/Warsaw:' + stamp(sunday - dt.timedelta(days=1), 10, False), properties=['RRULE:FREQ=DAILY;COUNT=4'])
    add('cancelled', 'Cancelled event', properties=['STATUS:CANCELLED'])
    rng = random.Random('events/' + account['key'])
    for index in range(12):
        offset = rng.randint(-20, 45)
        hour = rng.randint(8, 17)
        add(f'random-{index}', rng.choice(['Dentist', 'Sprint planning', 'Gym', 'Customer demo', 'Parent-teacher meeting', 'Flight to Berlin', 'Code review block']),
            start='DTSTART;TZID=Europe/Warsaw:' + stamp(day + dt.timedelta(days=offset), hour, False),
            end='DTEND;TZID=Europe/Warsaw:' + stamp(day + dt.timedelta(days=offset), hour, False, 45))
    return cases


def contact_uid(account, key):
    return 'urn:uuid:' + str(uuid.uuid5(uuid.NAMESPACE_URL, f"https://mailcal.lab/{account['key']}/{key}"))


def contacts(account):
    cases = []

    def add(key, title, version='3.0', properties=()):
        body = fold(['BEGIN:VCARD', 'VERSION:' + version, 'UID:' + contact_uid(account, key), 'FN:' + title,
            'N:' + title.split(' ', 1)[-1] + ';' + title.split(' ', 1)[0] + ';;;'] + list(properties) + ['END:VCARD'])
        cases.append((key, title, body))

    for other in ACCOUNTS:
        if other['key'] != account['key']:
            add('lab-' + other['key'], other['name'], properties=[f'EMAIL;TYPE=WORK:{address(other)}', 'ORG:Mail-Cal Lab', f"NOTE:{other['server']}"])
    for name, mail in EXTERNAL_PEOPLE:
        add('ext-' + mail.split('@')[0], name, properties=[f'EMAIL;TYPE=HOME:{mail}'])
    add('rich', 'Lab Rich Contact', properties=['EMAIL;TYPE=WORK:rich@mailcal.lab', 'EMAIL;TYPE=HOME:rich@example.test',
        'TEL;TYPE=CELL:+48123456789', 'TEL;TYPE=WORK:+48221234567', 'ADR;TYPE=HOME:;;Example Street 1;Warsaw;;00-001;Poland',
        'ADR;TYPE=WORK:;;Business Street 2;Berlin;;10115;Germany', 'ORG:Mail-Cal Lab;Engineering', 'TITLE:Developer',
        'BDAY:19900115', 'NOTE:Line one\\nLine two\\, punctuation.', 'URL:https://example.test/rich',
        'PHOTO;ENCODING=b;TYPE=PNG:' + base64.b64encode(PNG).decode()])
    add('unicode', 'Lab Zażółć 日本語 مرحبا', '4.0', ['EMAIL:unicode@mailcal.lab', 'TEL;VALUE=uri;TYPE=cell:tel:+48111222333', 'BDAY:--1015'])
    add('duplicate-a', 'Lab Same Name', properties=['EMAIL:shared@example.test'])
    add('duplicate-b', 'Lab Same Name', '4.0', ['EMAIL:shared@example.test'])
    add('group', 'Lab Team', '4.0', ['KIND:group', 'MEMBER:' + contact_uid(account, 'rich'), 'MEMBER:' + contact_uid(account, 'lab-dovecot' if account['key'] != 'dovecot' else 'lab-condstore')])
    return cases


def ensure_calendar(account, path, name):
    status, _ = dav(account, 'PROPFIND', path, headers={'Depth': '0'}, allowed=None)
    if status in (200, 207):
        return
    body = (f'<c:mkcalendar xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:set><d:prop><d:displayname>{name}</d:displayname>'
        '<c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set></d:prop></d:set></c:mkcalendar>').encode()
    dav(account, 'MKCALENDAR', path, body, {'Content-Type': 'application/xml; charset=utf-8'}, allowed=(200, 201, 204, 405))


def ensure_addressbook(account, path, name):
    status, _ = dav(account, 'PROPFIND', path, headers={'Depth': '0'}, allowed=None)
    if status in (200, 207):
        return
    body = (f'<d:mkcol xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:set><d:prop><d:resourcetype><d:collection/><c:addressbook/></d:resourcetype>'
        f'<d:displayname>{name}</d:displayname></d:prop></d:set></d:mkcol>').encode()
    dav(account, 'MKCOL', path, body, {'Content-Type': 'application/xml; charset=utf-8'}, allowed=(200, 201, 204, 405))


def seed_dav(day, catalog):
    for account in ACCOUNTS:
        counts = {'events': 0, 'contacts': 0}
        for collection in ('personal', 'work'):
            path = account['cal_home'] + collection + '/'
            ensure_calendar(account, path, f"{account['name']} {collection.title()}")
            for index, (key, title, content) in enumerate(events(day, account)):
                if (index % 2 == 0) != (collection == 'personal'):
                    continue
                dav(account, 'PUT', path + key + '.ics', content.encode(), {'Content-Type': 'text/calendar; charset=utf-8', 'If-None-Match': '*'}, allowed=(200, 201, 204, 412))
                counts['events'] += 1
        for collection in ('contacts', 'team'):
            path = account['card_home'] + collection + '/'
            ensure_addressbook(account, path, f"{account['name']} {collection.title()}")
            for index, (key, title, content) in enumerate(contacts(account)):
                if (collection == 'team') != key.startswith('ext-'):
                    continue
                dav(account, 'PUT', path + key + '.vcf', content.encode(), {'Content-Type': 'text/vcard; charset=utf-8', 'If-None-Match': '*'}, allowed=(200, 201, 204, 412))
                counts['contacts'] += 1
        catalog[account['key']].update(counts)
        log(f"  {account['key']}: {counts['events']} events, {counts['contacts']} contacts in {account['dav']}")


# ----------------------------------------------------------------------------------------------
# Provisioning, readiness, verification
# ----------------------------------------------------------------------------------------------
def provision():
    admin = ('admin', PASSWORD)
    stalwart = 'http://stalwart:8080'
    for body in ({'type': 'domain', 'name': 'stalwart.test', 'description': 'Mail-Cal lab'},
                 {'type': 'individual', 'name': 'carol@stalwart.test', 'secrets': [PASSWORD], 'emails': ['carol@stalwart.test'],
                  'description': 'Carol Stalwart', 'roles': ['user']}):
        status, data = http('POST', stalwart + '/api/principal', json.dumps(body).encode(), {'Content-Type': 'application/json'}, admin, allowed=None)
        text = data.decode(errors='replace')
        if status != 200 and 'already' not in text.lower() and 'exists' not in text.lower():
            raise RuntimeError(f'Stalwart principal {body["name"]}: {status} {text[:300]}')
    log('  stalwart: domain and account ready')
    james = 'http://james:8000'
    admin_header = {'Password': PASSWORD}
    http('PUT', james + '/domains/james.test', headers=admin_header, allowed=(200, 204, 400, 409))
    http('PUT', james + '/users/eve@james.test', json.dumps({'password': PASSWORD}).encode(),
        {'Content-Type': 'application/json', **admin_header}, allowed=(200, 204, 409))
    log('  james: domain and account ready')


def check_account(account):
    with imap(account):
        pass
    with smtp(account):
        pass
    status, _ = dav(account, 'PROPFIND', '', headers={'Depth': '0'}, allowed=None)
    if status in (401, 403):
        raise RuntimeError(f"DAV authentication failed for {account['dav_user']} ({status})")


def wait_ready(seconds=240):
    deadline = time.monotonic() + seconds
    pending = {account['key'] for account in ACCOUNTS}
    last = {}
    while pending and time.monotonic() < deadline:
        for key in sorted(pending):
            try:
                check_account(BY_KEY[key])
                pending.discard(key)
                log(f'  {key}: IMAP, SMTP, and DAV ready')
            except (OSError, imaplib.IMAP4.error, smtplib.SMTPException, RuntimeError) as error:
                last[key] = str(error)
        if pending:
            time.sleep(4)
    if pending:
        raise RuntimeError('Readiness timed out: ' + '; '.join(f'{key}: {last.get(key)}' for key in sorted(pending)))


def inbox_message_ids(client):
    """Map Message-ID to UID for every message in the selected folder."""
    found = {}
    status, data = client.uid('FETCH', '1:*', '(UID BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])')
    if status != 'OK':
        return found
    # Servers place the UID item before or after the literal; look at the tuple and the bytes that follow it.
    for index, part in enumerate(data):
        if not isinstance(part, tuple):
            continue
        trailer = data[index + 1] if index + 1 < len(data) and isinstance(data[index + 1], bytes) else b''
        uid = re.search(rb'UID (\d+)', part[0]) or re.search(rb'UID (\d+)', trailer)
        identifier = re.search(rb'<[^>]+>', part[1])
        if uid and identifier:
            found[identifier.group(0).decode()] = uid.group(1).decode()
    return found


def strategy(caps):
    upper = {cap.upper() for cap in caps}
    if 'QRESYNC' in upper:
        return 'QResync'
    if 'CONDSTORE' in upper:
        return 'Condstore'
    return 'UidBased'


def verify(catalog):
    results = {}
    log('Capabilities:')
    for account in ACCOUNTS:
        caps = capabilities(account)
        upper = {cap.upper() for cap in caps}
        expected = account['expect']
        verdict = 'probed'
        if expected is not None:
            mismatches = [name for name, wanted in expected.items() if (name in upper) != wanted]
            verdict = 'ok' if not mismatches else 'MISMATCH ' + ','.join(mismatches)
        results[account['key']] = {'capabilities': caps, 'expectedStrategy': strategy(caps), 'verdict': verdict}
        log(f"  {account['key']:<10} {strategy(caps):<10} {verdict:<12} {' '.join(caps)}")
        if verdict.startswith('MISMATCH'):
            raise RuntimeError(f"{account['key']} advertises unexpected capabilities: {' '.join(caps)}")
    log('Cross-server delivery probes:')
    for account in ACCOUNTS:
        # Remove probes left behind by earlier runs so inboxes only hold generated fixtures.
        junk = catalog.get(account['key'], {}).get('folders', {}).get('junk')
        with imap(account) as client:
            for folder in ['INBOX'] + ([junk] if junk else []):
                if client.select(quote(folder))[0] != 'OK':
                    continue
                stale = [uid for identifier, uid in inbox_message_ids(client).items() if identifier.startswith('<probe.')]
                if stale:
                    client.uid('STORE', ','.join(stale), '+FLAGS', r'(\Deleted)')
                    client.expunge()
    probes = {}
    for sender in ACCOUNTS:
        with smtp(sender) as client:
            for recipient in ACCOUNTS:
                if recipient['key'] == sender['key']:
                    continue
                msg = EmailMessage(policy=email.policy.SMTP)
                msg['From'] = display(sender)
                msg['To'] = display(recipient)
                msg['Subject'] = f"Lab probe {sender['key']} -> {recipient['key']}"
                msg['Message-ID'] = f"<probe.{sender['key']}.{recipient['key']}.{time.time_ns()}@mailcal.lab>"
                msg['Date'] = email.utils.format_datetime(dt.datetime.now(dt.timezone.utc))
                msg.set_content('Delivery probe from the Mail-Cal local lab.\n')
                client.send_message(msg, from_addr=address(sender), to_addrs=[address(recipient)])
                probes.setdefault(recipient['key'], []).append(msg['Message-ID'])
    deadline = time.monotonic() + 120
    missing = {}
    for recipient in ACCOUNTS:
        wanted = set(probes[recipient['key']])
        folders = ['INBOX']
        junk = catalog.get(recipient['key'], {}).get('folders', {}).get('junk')
        if junk:
            folders.append(junk)
        with imap(recipient) as client:
            while wanted and time.monotonic() < deadline:
                for folder in folders:
                    if client.select(quote(folder))[0] != 'OK':
                        continue
                    # Servers disagree on SEARCH HEADER quoting, so read the Message-ID headers directly.
                    for identifier, uid in inbox_message_ids(client).items():
                        if identifier in wanted:
                            client.uid('STORE', uid, '+FLAGS', r'(\Deleted)')
                            wanted.discard(identifier)
                            if folder != 'INBOX':
                                log(f"  note: {recipient['key']} filed {identifier} into {folder}")
                    try:
                        client.expunge()
                    except Exception:
                        pass
                if wanted:
                    time.sleep(2)
        if wanted:
            missing[recipient['key']] = sorted(wanted)
        log(f"  {recipient['key']}: {len(probes[recipient['key']]) - len(wanted)}/{len(probes[recipient['key']])} probes received")
    results['delivery'] = {'missing': missing}
    if missing:
        raise RuntimeError('Undelivered probes: ' + json.dumps(missing))
    return results


def write_fixtures(day, catalog, results):
    accounts = []
    for account in ACCOUNTS:
        entry = catalog.get(account['key'], {})
        verification = results.get(account['key'], {})
        accounts.append({
            'key': account['key'], 'user': account['user'], 'name': account['name'], 'address': address(account), 'password': PASSWORD,
            'server': account['server'], 'imapHost': '127.0.0.1', 'imapPort': account['host']['imapPort'], 'imapSecurity': 'None',
            'imapAuth': 'NormalPassword', 'smtpHost': '127.0.0.1', 'smtpPort': account['host']['smtpPort'], 'smtpSecurity': 'None',
            'smtpAuth': account['host']['smtpAuth'], 'davUrl': account['host']['davUrl'],
            'cardDavUrl': account['host'].get('cardDavUrl', account['host']['davUrl']), 'davUsername': account['dav_user'],
            'calendarHome': account['host']['calendarHome'], 'addressBookHome': account['host']['addressBookHome'],
            'capabilities': verification.get('capabilities'), 'expectedStrategy': verification.get('expectedStrategy'),
            'separator': entry.get('separator'), 'folders': entry.get('folders'), 'messageCount': entry.get('messageCount'),
            'perFolder': entry.get('perFolder'), 'events': entry.get('events'), 'contacts': entry.get('contacts'),
        })
    save_json(STATE / 'fixtures.json', {'anchorDate': day.isoformat(), 'password': PASSWORD, 'hubSmtpPort': 4025, 'accounts': accounts})


if __name__ == '__main__':
    STATE.mkdir(parents=True, exist_ok=True)
    command = sys.argv[1] if len(sys.argv) > 1 else 'all'
    day = anchor()
    catalog_path = STATE / 'catalog.json'
    catalog = json.loads(catalog_path.read_text()) if catalog_path.exists() else {}
    results_path = STATE / 'verify.json'
    results = json.loads(results_path.read_text()) if results_path.exists() else {}
    if command in ('provision', 'all'):
        log('Provisioning accounts on Stalwart and James')
        provision()
    if command in ('ready', 'all', 'seed', 'verify'):
        log('Waiting for IMAP, SMTP, and DAV readiness')
        wait_ready()
    if command in ('seed', 'all'):
        log(f'Seeding mail (anchor {day})')
        seed_mail(day, catalog)
        log('Seeding calendars and contacts')
        seed_dav(day, catalog)
        save_json(catalog_path, catalog)
    if command in ('verify', 'all'):
        results = verify(catalog)
        save_json(results_path, results)
    if command not in ('provision', 'ready', 'seed', 'verify', 'all'):
        raise ValueError(f'Unknown command {command}')
    write_fixtures(day, catalog, results)
    log(f'{command} complete; fixtures written to /state/fixtures.json')
