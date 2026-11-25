"""Builds the Microsoft Store listing images.

    python scripts/release/store-listing/build.py              # English, light by default, into out/
    python scripts/release/store-listing/build.py mail todo    # only scenes whose name contains "mail" or "todo"
    python scripts/release/store-listing/build.py --lang de-de # one language from i18n/, into out/de-de
    python scripts/release/store-listing/build.py --lang all   # every language in i18n/
    python scripts/release/store-listing/build.py --set dark   # the dark-by-default set, into out/dark
    python scripts/release/store-listing/build.py --art        # hero, box and poster art, into out/art
    python scripts/release/store-listing/build.py --mac        # the Mac App Store set (2880x1800) from screens/macos, into out/macos

Each scene is a 1920x1080 canvas rendered at 2x (3840x2160). A scene with an app window shows
screens/<scene>.png (dark) or screens/<scene>-light.png when that file exists, and the drawn placeholder otherwise.
A set is dark or light by default and has one Mail image in the other theme.
"""
import json
import math
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
ICONS = REPO / "icons" / "svg"
ASSETS = "../../../src/Wino.Mail.WinUI/Assets"
THEMES = "../../../src/Wino.Mail.WinUI/BackgroundImages"
EDGE = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")


def ico(name, color=None, cls=""):
    svg = (ICONS / f"{name}.svg").read_text(encoding="utf-8")
    paths = "".join(re.findall(r"<path[^>]*/>", svg))
    style = f' style="color:{color}"' if color else ""
    return f'<svg class="i {cls}" viewBox="0 0 1024 1024"{style}>{paths}</svg>'


def av(initials, color, cls=""):
    return f'<span class="av {cls}" style="background:{color}">{initials}</span>'


def app(name):
    return f'<img class="applogo" src="{ASSETS}/AppEntries/{name}.svg" alt="">'


def provider(name):
    return f'<img src="{ASSETS}/Providers/{name}.png" alt="">'


def chip(text, icon=""):
    return f'<span class="chip">{icon}{text}</span>'


def caption(headline, sub="", badges=""):
    extra = (f"<p>{sub}</p>" if sub else "") + (f'<div class="badges">{badges}</div>' if badges else "")
    if LAYOUT in ("full", "zoom", "fan"):
        headline = headline.replace("<br>", " ")  # one line above the app, so the app gets the height
    lines = " l3" if headline.count("<br>") >= 2 else ""
    return f'<header class="cap{lines}"><h1>{headline}</h1>{extra}</header>'


def badge(icon, text):
    return f'<span class="badge">{ico(icon)}{text}</span>'


def titlebar(logo, title, search="Search"):
    return f"""
    <div class="tb">
      <div class="tb-l">{ico('Hamburger')}{app(logo)}<span>{title}</span></div>
      <div class="search">{ico('Find')}<span>{search}</span></div>
      <div class="tb-r">{ico('Announcement')}{av('A', 'var(--purple)', 's')}
        <span class="capbtn">&#xE921;</span><span class="capbtn">&#xE922;</span><span class="capbtn">&#xE8BB;</span></div>
    </div>"""


def modes(active):
    items = [("mail", "AppModeMail"), ("calendar", "AppModeCalendar"), ("people", "AppModeContacts"), ("todo", "AppModeToDo")]
    out = "".join(f'<span class="mode{" on" if k == active else ""}">{ico(i)}</span>' for k, i in items)
    return f'<div class="modes">{out}<span class="mode end">{ico("Settings")}</span></div>'


def nav(rows):
    out = ""
    for r in rows:
        if r is None:
            out += '<div class="sep"></div>'
        elif isinstance(r, str):
            out += f'<div class="nav-h">{r}</div>'
        else:
            icon, label, *rest = r
            count = rest[0] if rest else ""
            on = " on" if len(rest) > 1 and rest[1] else ""
            out += f'<div class="nav-i{on}">{icon}<span>{label}</span><b>{count}</b></div>'
    return out


def window(shot, body):
    """The app window: a capture from screens/ when there is one, the drawn placeholder otherwise."""
    name = f'macos/{shot}{"-dark" if DARK else ""}.png' if MAC else f'{shot}{"" if DARK else "-light"}.png'
    if not (HERE / "screens" / name).exists():
        return f'<div class="window"><div class="mimic">{body}</div></div>'
    return f'<div class="window"><img class="shot" src="screens/{name}" alt=""></div>'


# --------------------------------------------------------------------------------------- Mail

def scene_mail():
    folders = nav([
        (ico("SpecialFolderInbox", "var(--blue)"), "Inbox", "12", True),
        (ico("SpecialFolderStarred", "var(--amber)"), "Starred"),
        (ico("SpecialFolderDraft", "var(--teal)"), "Drafts", "2"),
        (ico("SpecialFolderSent", "var(--green)"), "Sent"),
        (ico("SpecialFolderArchive", "var(--purple)"), "Archive"),
        (ico("SpecialFolderDeleted", "var(--red)"), "Deleted"),
        (ico("SpecialFolderJunk", "var(--orange)"), "Junk"),
    ])
    mails = [
        ("Today", None),
        ("AC", "var(--purple)", "Avery Chen", "Project kickoff: agenda and next steps", "Hi all, here is the agenda for Thursday. Please accept the invitation…", "10:12", "on unread", "cal"),
        ("NW", "var(--teal)", "Northwind Travel", "Your booking is confirmed", "Lisbon, 4 nights. Check-in opens 24 hours before departure.", "09:40", "unread", "att"),
        ("JR", "var(--orange)", "Jordan Rivera", "Re: Design review notes", "Thanks for the quick turnaround. Two small things on the settings page…", "08:55", "", "flag"),
        ("Yesterday", None),
        ("CB", "var(--green)", "Contoso Bank", "Your October statement is ready", "Your statement for the period ending 31 October is now available.", "Sun", "", "att"),
        ("MP", "var(--blue)", "Morgan Patel", "Lunch on Friday?", "There is a new place near the office. 12:30 works for me.", "Sun", "unread", ""),
        ("FT", "var(--red)", "Fabrikam Tools", "Your order has shipped", "Order 48213 is on its way. Track your package for delivery updates.", "Sun", "", ""),
        ("Last week", None),
        ("SK", "var(--amber)", "Sam Kim", "Photos from the weekend", "Finally sorted them. The one from the lookout is my favorite.", "Fri", "", "att"),
        ("WU", "var(--gray)", "Woodgrove University", "Alumni newsletter: November", "Events near you, a new mentoring program and campus news.", "Thu", "", ""),
    ]
    rows = ""
    for m in mails:
        if m[1] is None:
            rows += f'<div class="grp">{m[0]}</div>'
            continue
        ini, col, sender, subject, preview, time, state, mark = m
        marks = {"cal": ico("Calendar", "var(--accent)"), "att": ico("Attachment"), "flag": ico("Flag", "var(--red)"), "": ""}[mark]
        rows += f"""<div class="mail {state}">{av(ini, col)}
          <div class="mail-t"><div class="l1"><span>{sender}</span><em>{marks}{time}</em></div>
          <div class="l2">{subject}</div><div class="l3">{preview}</div></div></div>"""
    body = f"""
    {titlebar('Mail', 'Wino Mail')}
    <div class="shell">
      <aside class="pane">
        <div class="btn accent wide">{ico('NewMail')}New mail</div>
        <div class="acct on">{av('A', 'var(--purple)')}<div><strong>All inboxes</strong><small>3 accounts</small></div><b>12</b></div>
        <div class="acct">{provider('Outlook')}<div><strong>Personal</strong><small>alex@outlook.com</small></div></div>
        <div class="acct">{provider('Gmail')}<div><strong>Side project</strong><small>alex@gmail.com</small></div></div>
        <div class="acct">{provider('Exchange')}<div><strong>{T['vb_account']}</strong><small>alex@contoso.com</small></div></div>
        <div class="sep"></div>
        {folders}
        {modes('mail')}
      </aside>
      <section class="list">
        <div class="list-h"><h2>Inbox</h2><span class="pillbtn">{ico('Filter')}All</span></div>
        {rows}
      </section>
      <section class="reader">
        <div class="cmd">
          <span>{ico('Reply')}Reply</span><span>{ico('ReplyAll')}Reply all</span><span>{ico('Forward')}Forward</span>
          <span>{ico('Archive')}Archive</span><span>{ico('Delete')}Delete</span><span>{ico('Flag')}Flag</span>
          <span>{ico('Sparkle', 'var(--purple)')}Summarize</span><span class="end">{ico('More')}</span>
        </div>
        <div class="reader-b">
          <h2>Project kickoff: agenda and next steps</h2>
          <div class="from">{av('AC', 'var(--purple)', 'l')}<div><strong>Avery Chen</strong><small>to Alex, Jordan, Morgan</small></div><time>Mon 16 Nov, 10:12</time></div>
          <div class="invite">
            <div class="invite-d"><small>NOV</small><strong>19</strong></div>
            <div class="invite-t"><strong>Project kickoff</strong><small>Thursday, 10:00 – 11:00 · Conference room 2</small></div>
            <span class="btn accent">{ico('EventAccept')}Accept</span><span class="btn">{ico('EventTentative')}Tentative</span><span class="btn">{ico('EventDecline')}Decline</span>
          </div>
          <p>Hi all,</p>
          <p>Thanks for making time this week. Here is the agenda for Thursday, so we can use the hour well and leave with clear owners.</p>
          <ol><li>Goals for the first milestone</li><li>Design review of the new onboarding</li><li>Timeline, risks and who owns what</li></ol>
          <p>I attached the draft plan. Have a look before the meeting and add your comments directly in the document.</p>
          <p>Talk soon,<br>Avery</p>
          <div class="att">{ico('DocumentPdf', 'var(--red)')}<div><strong>Kickoff plan.pdf</strong><small>248 KB</small></div></div>
        </div>
      </section>
    </div>"""
    return caption(T["head_mail"]) + window("mail", body)


# ----------------------------------------------------------------------------------- Calendar

def scene_calendar():
    days = ["Mon 16", "Tue 17", "Wed 18", "Thu 19", "Fri 20", "Sat 21", "Sun 22"]
    events = [
        (0, 9.0, 9.5, "Stand-up", "blue"), (0, 11.0, 12.0, "Design review", "purple"), (0, 14.0, 15.5, "Focus time", "teal"),
        (1, 9.0, 9.5, "Stand-up", "blue"), (1, 10.0, 11.5, "Customer call", "orange"), (1, 13.0, 14.0, "Lunch with Morgan", "green"),
        (2, 9.0, 9.5, "Stand-up", "blue"), (2, 12.0, 13.0, "Dentist", "red"), (2, 15.0, 16.0, "1:1 with Jordan", "purple"),
        (3, 9.0, 9.5, "Stand-up", "blue"), (3, 10.0, 11.0, "Project kickoff", "purple"), (3, 13.5, 15.0, "Workshop", "orange"),
        (4, 9.0, 9.5, "Stand-up", "blue"), (4, 12.5, 13.5, "Team lunch", "green"), (4, 16.0, 17.0, "Week wrap-up", "teal"),
        (5, 10.0, 12.0, "Farmers market", "green"), (6, 11.0, 13.0, "Brunch", "amber"),
    ]
    hours = "".join(f'<div class="hr"><span>{h if h <= 12 else h - 12} {"AM" if h < 12 else "PM"}</span></div>' for h in range(8, 22))
    cols = ""
    for d in range(7):
        evs = "".join(
            f'<div class="ev {c}" style="top:{(s - 8) * 68}px;height:{(e - s) * 68 - 3}px"><strong>{t}</strong>'
            f'{"" if e - s <= 0.5 else f"<small>{int(s)}:{chr(51) if s % 1 else chr(48)}0</small>"}</div>'
            for dd, s, e, t, c in events if dd == d)
        now = '<div class="now"></div>' if d == 0 else ""
        cols += f'<div class="daycol{" today" if d == 0 else ""}">{evs}{now}</div>'
    heads = "".join(f'<div class="dh{" today" if i == 0 else ""}"><small>{d.split()[0]}</small><strong>{d.split()[1]}</strong></div>' for i, d in enumerate(days))
    mini = "".join(f"<span>{d}</span>" for d in "MTWTFSS")
    first = 26
    cells = [str(x) for x in list(range(26, 32)) + list(range(1, 31)) + list(range(1, 7))]
    for i, c in enumerate(cells):
        cls = "dim" if i < 6 or i >= 36 else ("sel" if c == "16" else ("wk" if 17 <= int(c) <= 22 else ""))
        mini += f'<span class="{cls}">{c}</span>'
    cals = "".join(f'<div class="cal"><i style="background:var(--{c})"></i><span>{n}</span></div>' for n, c in
                   [("Calendar", "blue"), ("Team", "purple"), ("Family", "green"), ("Birthdays", "amber"), ("Holidays", "red"), ("Personal (CalDAV)", "teal")])
    body = f"""
    {titlebar('Calendar', 'Wino Calendar')}
    <div class="shell">
      <aside class="pane">
        <div class="btn accent wide">{ico('Add')}New event</div>
        <div class="mini-h"><strong>November 2026</strong>{ico('ChevronLeft')}{ico('ChevronRight')}</div>
        <div class="mini">{mini}</div>
        <div class="sep"></div>
        <div class="nav-h">Calendars</div>
        {cals}
        {modes('calendar')}
      </aside>
      <section class="calendar">
        <div class="cal-bar">
          <span class="btn">{ico('CalendarToday')}Today</span>{ico('ChevronLeft')}{ico('ChevronRight')}
          <h2>November 16 – 22, 2026</h2>
          <div class="views"><span>{ico('CalendarDay')}Day</span><span class="on">{ico('CalendarWeek')}Week</span><span>{ico('CalendarWorkWeek')}Work week</span><span>{ico('CalendarMonth')}Month</span></div>
        </div>
        <div class="cal-heads"><div class="gutter"></div>{heads}</div>
        <div class="cal-grid"><div class="gutter">{hours}</div>{cols}</div>
      </section>
    </div>"""
    return caption(T["head_calendar"]) + window("calendar", body)


# ------------------------------------------------------------------------------------- People

def scene_people():
    people = [
        ("A", None), ("AC", "purple", "Avery Chen", "Contoso", True, True), ("AD", "teal", "Alex Dvorak", "Northwind Travel", False, False),
        ("J", None), ("JR", "orange", "Jordan Rivera", "Contoso", False, True), ("JL", "blue", "Jamie Lindqvist", "Fabrikam", False, False),
        ("M", None), ("MP", "blue", "Morgan Patel", "Woodgrove", False, True), ("MO", "green", "Mika Okafor", "Tailspin Toys", False, False),
        ("R", None), ("RS", "red", "Riley Santos", "Contoso", False, False),
        ("S", None), ("SK", "amber", "Sam Kim", "Family", False, True), ("SN", "purple", "Sasha Novak", "Adventure Works", False, False),
    ]
    rows = ""
    for p in people:
        if p[1] is None:
            rows += f'<div class="grp">{p[0]}</div>'
            continue
        ini, col, name, org, on, fav = p
        rows += f"""<div class="mail{' on' if on else ''}">{av(ini, f'var(--{col})')}
          <div class="mail-t"><div class="l1"><span>{name}</span><em>{ico('StarFilled', 'var(--amber)') if fav else ''}</em></div><div class="l3">{org}</div></div></div>"""
    navrows = nav([
        (ico("People", "var(--blue)"), "All contacts", "128", True),
        (ico("StarFilled", "var(--amber)"), "Favorites", "9"),
        None, "Address books",
        (provider("Outlook"), "Personal", "64"), (provider("Gmail"), "Side project", "41"), (provider("Exchange"), "Work", "23"),
        None, "Lists",
        (ico("GroupList", "var(--teal)"), "Design team", "6"), (ico("GroupList", "var(--teal)"), "Book club", "8"),
        None, "Categories",
        (ico("Tag", "var(--green)"), "Family"), (ico("Tag", "var(--orange)"), "Clients"),
    ])

    def field(icon, label, value):
        return f'<div class="field">{ico(icon)}<div><small>{label}</small><strong>{value}</strong></div></div>'

    body = f"""
    {titlebar('People', 'Wino People', 'Search contacts')}
    <div class="shell">
      <aside class="pane"><div class="btn accent wide">{ico('PersonAdd')}New contact</div>{navrows}{modes('people')}</aside>
      <section class="list"><div class="list-h"><h2>All contacts</h2><span class="pillbtn">{ico('SortTextDesc')}Name</span></div>{rows}</section>
      <section class="reader">
        <div class="cmd"><span>{ico('Mail')}Send mail</span><span>{ico('Calendar')}New event</span><span>{ico('Edit')}Edit</span><span>{ico('StarFilled', 'var(--amber)')}Favorite</span><span class="end">{ico('More')}</span></div>
        <div class="reader-b">
          <div class="hero-c">{av('AC', 'var(--purple)', 'xl')}<div><h2>Avery Chen</h2><p class="muted">Product designer · Contoso</p>
            <div class="tags"><span class="tag green">Clients</span><span class="tag teal">Design team</span></div></div></div>
          <div class="cards">
            <div class="card"><h3>Contact</h3>
              {field('Mail', 'Work', 'avery.chen@contoso.com')}{field('Mail', 'Personal', 'avery@fabrikam.com')}
              {field('Phone', 'Mobile', '+1 555 0142')}{field('Phone', 'Work', '+1 555 0198')}</div>
            <div class="card"><h3>Details</h3>
              {field('Briefcase', 'Company', 'Contoso · Design')}{field('Location', 'Address', '4567 Main Street, Seattle')}
              {field('Calendar', 'Birthday', '14 March')}{field('Note', 'Notes', 'Prefers a call on Fridays.')}</div>
          </div>
        </div>
      </section>
    </div>"""
    return caption(T["head_people"]) + window("people", body)


# -------------------------------------------------------------------------------------- To Do

def scene_todo():
    navrows = nav([
        (ico("WeatherSunny", "var(--amber)"), "My Day", "4", True),
        (ico("StarFilled", "var(--amber)"), "Important", "2"),
        (ico("CalendarMonth", "var(--teal)"), "Planned", "7"),
        (ico("TaskList", "var(--blue)"), "All", "18"),
        (ico("CheckmarkCircle", "var(--green)"), "Completed"),
        None, "Lists",
        (ico("List", "var(--blue)"), "Work", "9"), (ico("List", "var(--green)"), "Groceries", "5"),
        (ico("List", "var(--purple)"), "Home", "3"), (ico("List", "var(--orange)"), "Trip to Lisbon", "4"),
    ])
    tasks = [
        ("Prepare kickoff slides", f"{ico('Calendar')}Today · 1 of 3", True, False, True),
        ("Book team lunch", f"{ico('WeatherSunny')}My Day · Work", False, False, False),
        ("Renew passport", f"{ico('Reminder')}Tomorrow, 9:00 · Home", True, False, False),
        ("Send invoice to Northwind", f"{ico('Calendar')}Fri 20 Nov · Work", False, False, False),
        ("Reply to Avery", f"{ico('WeatherSunny')}My Day", False, True, False),
    ]
    rows = ""
    for title, meta, star, done, on in tasks:
        check = f'<span class="chk done">{ico("Checkmark")}</span>' if done else '<span class="chk"></span>'
        rows += f"""<div class="task{' on' if on else ''}{' done' if done else ''}">{check}<div><strong>{title}</strong><small>{meta}</small></div>
          {ico('StarFilled', 'var(--amber)') if star else ico('Star')}</div>"""
    steps = "".join(f'<div class="step{" done" if d else ""}"><span class="chk s{" done" if d else ""}">{ico("Checkmark") if d else ""}</span>{t}</div>'
                    for t, d in [("Collect the numbers from Jordan", True), ("Draft the timeline slide", False), ("Rehearse once", False)])
    body = f"""
    {titlebar('ToDo', 'Wino To Do', 'Search tasks')}
    <div class="shell">
      <aside class="pane"><div class="btn accent wide">{ico('Add')}New task</div>{navrows}{modes('todo')}</aside>
      <section class="tasks">
        <div class="tasks-h"><h2>My Day</h2><p class="muted">Monday, 16 November</p></div>
        {rows}
        <div class="addtask">{ico('Add')}Add a task</div>
      </section>
      <section class="detail">
        <div class="task flat"><span class="chk"></span><div><strong>Prepare kickoff slides</strong></div>{ico('StarFilled', 'var(--amber)')}</div>
        <div class="card">{steps}<div class="step add">{ico('Add')}Add step</div></div>
        <div class="card rows">
          <div>{ico('WeatherSunny', 'var(--amber)')}Added to My Day</div>
          <div>{ico('Calendar', 'var(--blue)')}Due today</div>
          <div>{ico('Reminder', 'var(--purple)')}Remind me at 14:00</div>
          <div>{ico('ArrowRepeatAll', 'var(--teal)')}Repeat</div>
        </div>
        <div class="card note"><small>Notes</small>Keep it to ten slides. Avery asked for the timeline first.</div>
      </section>
    </div>"""
    return caption(T["head_todo"]) + window("todo", body)


# ------------------------------------------------------------------------------------- Themes

def scene_themes():
    if MAC:
        fan = "".join(f'<div class="window"><img class="shot" src="screens/macos/theme-{n}.png" alt=""></div>' for n in MAC_THEMES)
        return caption(T["head_themes"]) + f'<div class="fan">{fan}</div>'
    names = ["Aurora", "Nebula", "Coastline", "Forest", "Dunes", "Skyline", "Lavender", "Ember", "Blossom", "Mist", "Indigo", "Garden"]
    tiles = "".join(f'<div class="theme{" on" if n == "Aurora" else ""}"><img src="{THEMES}/{n}.jpg" alt=""><span>{n}</span></div>' for n in names)
    swatches = "".join(f'<i style="background:{c}"{" class=on" if k == 0 else ""}></i>' for k, c in enumerate(
        ["#60cdff", "#4ca8ff", "#b28cff", "#f472b6", "#ff6b6b", "#f7894a", "#f7c948", "#6ccb5f", "#3fd1c6", "#9aa3b5"]))
    navrows = nav([
        (ico("Settings"), "General"), (ico("PaintBrush", "var(--purple)"), "Personalization", "", True), (ico("ManageAccounts"), "Manage accounts"),
        (ico("Mail"), "Message list"), (ico("Edit"), "Reader &amp; Composer"), (ico("Calendar"), "Calendar"),
        (ico("Alert"), "Notifications"), (ico("Grid"), "Widgets"), (ico("Shield"), "Privacy &amp; security"),
        (ico("Sparkle"), "Wino Assistant"), (ico("Keyboard"), "Keyboard shortcuts"), (ico("Info"), "About"),
    ])
    body = f"""
    {titlebar('Mail', 'Settings', 'Search settings')}
    <div class="shell">
      <aside class="pane">{navrows}{modes('')}</aside>
      <section class="settings">
        <h2><span class="muted">Settings ›</span> Personalization</h2>
        <div class="card set">{ico('Color', 'var(--accent)')}<div><strong>Accent color</strong><small>Used for buttons, selection and links</small></div><div class="swatches">{swatches}</div></div>
        <div class="card set col">
          <div class="set-h">{ico('PaintBrush', 'var(--purple)')}<div><strong>Application themes</strong><small>Pick a theme, or create your own with a wallpaper and surface colors</small></div><span class="btn">{ico('Add')}Create theme</span></div>
          <div class="themes">{tiles}</div>
        </div>
        <div class="card set">{ico('IconStyle', 'var(--amber)')}<div><strong>Icon style</strong><small>Give folders, flags and calendar views a soft tint</small></div><span class="combo">Colorful{ico('ChevronDown')}</span></div>
      </section>
    </div>"""
    return caption(T["head_themes"]) + window("themes", body)


# ------------------------------------------------------------------------------------ Privacy

def scene_privacy():
    navrows = nav([
        (ico("Settings"), "General"), (ico("PaintBrush"), "Personalization"), (ico("ManageAccounts"), "Manage accounts"),
        (ico("Mail"), "Message list"), (ico("Edit"), "Reader &amp; Composer"), (ico("Calendar"), "Calendar"),
        (ico("Alert"), "Notifications"), (ico("Grid"), "Widgets"), (ico("Shield", "var(--green)"), "Privacy &amp; security", "", True),
        (ico("Sparkle"), "Wino Assistant"), (ico("Keyboard"), "Keyboard shortcuts"), (ico("Info"), "About"),
    ])

    def row(icon, color, title, sub, control):
        return f'<div class="card set">{ico(icon, f"var(--{color})")}<div><strong>{title}</strong><small>{sub}</small></div>{control}</div>'

    on = '<span class="toggle on"><b>On</b><i></i></span>'
    body = f"""
    {titlebar('Mail', 'Settings', 'Search settings')}
    <div class="shell">
      <aside class="pane">{navrows}{modes('')}</aside>
      <section class="settings">
        <h2><span class="muted">Settings ›</span> Privacy &amp; security</h2>
        <div class="infobar">{ico('Shield', 'var(--green)')}<div><strong>Wino blocked 214 trackers this month</strong><small>Senders can't see when or where you open their mail.</small></div></div>
        {row('EyeOff', 'blue', 'Block email trackers', 'Remove tracking pixels before a message is shown', on)}
        {row('Image', 'purple', 'Remote content', 'Choose whether pictures and other remote content load', f'<span class="combo">Ask every time{ico("ChevronDown")}</span>')}
        {row('LinkDismiss', 'teal', 'Clean tracking from links', 'Open links without the parameters that follow you around', on)}
        {row('Warning', 'amber', 'Warn before risky attachments', 'Show a warning before a file that can run code is opened', on)}
        {row('PersonLock', 'orange', 'Sender and domain rules', 'Always allow or always block remote content for the senders you choose', f'<span class="muted">6 rules</span>{ico("ChevronRight")}')}
        {row('Certificate', 'green', 'Trusted server certificates', 'Review the certificates you chose to trust for your own servers', ico("ChevronRight"))}
      </section>
    </div>"""
    return caption(T["head_privacy"]) + window("privacy", body)


# ---------------------------------------------------------------------------------- Assistant

def taskbar():
    """A default Windows 11 taskbar: Start, Search and Task View, with Wino running, and the system tray."""
    start = '<span class="tk"><span class="tk-start"><i></i><i></i><i></i><i></i></span></span>'
    search = f'<span class="tk">{ico("Find")}</span>'
    views = '<span class="tk"><span class="tk-views"><i></i><i></i></span></span>'
    wino = f'<span class="tk on">{app("Mail")}</span>'
    tray = ('<div class="tk-tray"><span class="tk-glyph">&#xE70E;</span><span class="tk-glyph">&#xE701;</span><span class="tk-glyph">&#xE767;</span>'
            '<span class="tk-clock">10:12 AM<br>11/17/2026</span></div>')
    return f'<div class="taskbar"><div class="tk-center">{start}{search}{views}{wino}</div>{tray}</div>'


def flyout(shot, body):
    name = f'{shot}{"" if DARK else "-light"}.png'
    if (HERE / "screens" / name).exists():
        return f'<div class="flyout"><img class="shot" src="screens/{name}" alt=""></div>'
    return f'<div class="flyout" data-shot="{shot}"><div class="ph">{body}</div></div>'


def voice_bars(count=40):
    """The listening bars of WinoVoiceBubble: 3 to 23 px tall, in a 24 px row."""
    heights = [3 + 20 * abs(math.sin(i * 0.55) * math.sin(i * 0.17 + 0.6)) ** 0.8 for i in range(count)]
    return '<div class="vb-bars">' + "".join(f'<i style="height:{h:.1f}px"></i>' for h in heights) + "</div>"


def voice_bubble(header, body="", cls=""):
    logo = f'<span class="vb-icon">{app("Mail")}</span>'
    return f'<div class="vb {cls}"><div class="vb-h">{logo}{header}</div>{body}</div>'


def scene_assistant():
    """Three states of the voice bubble (controls/Wino.Mail.Controls.WinUI/VoiceBubble), drawn at twice their size."""
    listening = voice_bubble(voice_bars(), f'<p class="vb-said"><span>{T["vb_said_heard"]}</span> {T["vb_said_rest"]}</p>')
    steps = "".join(f'<div class="vb-step"><b>{n}</b><div><p>{text}</p>{f"<small>{detail}</small>" if detail else ""}</div></div>' for n, text, detail in [
        (1, T["vb_step1"], T["vb_step1_detail"]), (2, T["vb_step2"], ""), (3, T["vb_step3"], T["vb_step3_detail"]),
    ])
    plan = voice_bubble(f'<p class="vb-ask">{T["vb_ask"]}</p>', f"""
      <div class="vb-steps">
        <div class="vb-group">{provider('Outlook')}<div><strong>{T['vb_account']}</strong><small>alex@contoso.com</small></div></div>
        {steps}
        <div class="vb-actions"><span class="btn accent">{T['vb_yes']}</span><span class="btn">{T['vb_no']}</span></div>
      </div>""")
    done = voice_bubble(f'<span class="vb-ok">{ico("Checkmark")}</span><p class="vb-msg">{T["vb_done"]}</p>', cls="vb-done")
    body = f'<div class="vb-stack">{listening}{plan}{done}</div>'
    return caption(T["head_assistant"], T["assistant_sub"], badge("LockClosed", T["badge_privacy"]) + badge("Sparkle", T["badge_paid"])) + flyout("assistant", body) + taskbar()


# ---------------------------------------------------------------------------------- Companion

def scene_companion():
    mail = "".join(f'<div class="w-r">{av(i, f"var(--{c})")}<div><strong>{s}</strong><small>{t}</small></div><time>{h}</time></div>' for i, c, s, t, h in [
        ("AC", "purple", "Avery Chen", "Project kickoff: agenda and next steps", "10:12"),
        ("NW", "teal", "Northwind Travel", "Your booking is confirmed", "09:40")])
    todo = "".join(f'<div class="w-r"><span class="chk"></span><div><strong>{s}</strong><small>{t}</small></div></div>' for s, t in [
        ("Prepare kickoff slides", "Today · 1 of 3"), ("Book team lunch", "Work")])
    body = f"""
    <div class="widget companion">
      <div class="w-h">{app('Mail')}<strong>Monday, 16 November</strong>{ico('Settings')}</div>
      <div class="next"><i class="bar" style="background:var(--purple)"></i><div><small>Next meeting · in 40 minutes</small><strong>Design review</strong><small>11:00 – 12:00 · Room 2</small></div><span class="btn accent">{ico('EventJoinOnline')}Join</span></div>
      <div class="w-s">{ico('Mail', 'var(--blue)')}Unread mail<b>12</b></div>{mail}
      <div class="w-s">{ico('WeatherSunny', 'var(--amber)')}My Day<b>4</b></div>{todo}
    </div>"""
    return caption(T["head_companion"]) + flyout("companion", body) + taskbar()


# ----------------------------------------------------------------------------------- Features

def scene_features():
    icons = [("Flash", "amber"), ("Pulse", "green"), ("PeopleCommunity", "blue"), ("PersonSwap", "purple"), ("Keyboard", "teal"),
             ("Grid", "orange"), ("DocumentAdd", "blue"), ("Signature", "purple"), ("TextProofingTools", "red")]
    features = list(T["features"])
    if MAC:  # the Mac app has the menu bar Companion instead of Windows widgets and Live Tiles
        icons[5] = ("Companion", "orange")
        features[5] = T.get("feature_mac_companion", ["Menu bar Companion", "Your next event, tasks and mail one click away"])
    items = [(i, c, t, d) for (i, c), (t, d) in zip(icons, features)]
    cards = "".join(f'<div class="feat"><span style="color:var(--{c})">{ico(i)}</span><div><strong>{t}</strong><small>{d}</small></div></div>' for i, c, t, d in items)
    providers = "".join(f"<span>{provider(f)}{n}</span>" for n, f in [
        ("Outlook", "Outlook"), ("Gmail", "Gmail"), ("Exchange", "Exchange"), ("iCloud", "iCloud"), ("Yahoo", "Yahoo"), ("IMAP", "IMAP4"), ("POP3", "IMAP4")])
    return caption(T["head_features"]) + f'<div class="feats">{cards}</div><div class="providers">{providers}</div>'


# file name: (scene, layout, variant). A set is light or dark by default; a variant image is in the other theme.
# Layouts: full = one-line headline above the whole app window; zoom = one-line headline above an enlarged capture
# that runs off the bottom; top = headline above a drawn panel; side = headline beside a flyout over the taskbar.
SCENES = {
    "01-mail": ("mail", "full", False), "02-mail-dark": ("mail", "full", True),
    "03-calendar": ("calendar", "full", False), "04-calendar-dark": ("calendar", "full", True),
    "05-people": ("people", "zoom", False), "06-todo": ("todo", "zoom", False),
    "07-features": ("features", "top", False), "08-companion": ("companion", "side", False),
    "09-themes": ("themes", "zoom", False), "10-assistant": ("assistant", "side", False),
}

# Per scene: eyebrow text, backdrop icon, three backdrop colors, the two headline accent colors and the builder.
META = {
    "mail": ("Wino Mail", "Mail", "#9fd0ff", "#c5c0ff", "#aeeef2", "#0a56c2", "#5a48e0", scene_mail),
    "calendar": ("Wino Calendar", "CalendarMonth", "#9fe6de", "#a9d2ff", "#cdf0b4", "#00707a", "#0a66d6", scene_calendar),
    "people": ("Wino People", "People", "#d2c2ff", "#ffc0e6", "#b9cdff", "#5b3fd6", "#c2378f", scene_people),
    "todo": ("Wino To Do", "CheckmarkCircle", "#b6edbf", "#f6eca0", "#a8e6e8", "#0b7a3b", "#00808a", scene_todo),
    "features": ("Wino Mail", "Star", "#a9cbff", "#cdbcff", "#aeeef2", "#0a56c2", "#7a3be0", scene_features),
    "companion": ("Wino Companion", "Companion", "#ffd49a", "#ffb9bd", "#f4e6a2", "#c2500a", "#c8306a", scene_companion),
    "themes": ("Personalization", "PaintBrush", "#ffbcd8", "#ffdca6", "#c4ccff", "#cf2f7c", "#e0661a", scene_themes),
    "privacy": ("Privacy &amp; security", "Shield", "#a9e6cd", "#acd6f5", "#d2efb6", "#0a6b52", "#0a5fae", scene_privacy),
    "assistant": ("Wino Mail", "Sparkle", "#cdbcff", "#ffbfdf", "#a9cbff", "#6a3be0", "#d13a86", scene_assistant),
}
# The Mac App Store set: the same scenes with captures of the AppKit app (screens/macos, from the
# MacDebugBridge commands seedstore and snap2x). Mail and Calendar show the whole window; People and To Do are zoomed.
# The dark Mail and Calendar captures merge snap2x with a real window capture (wsnap), because offscreen rendering
# draws the dark toolbar's glass buttons and the pane dividers white.
SCENES_MAC = {
    "01-mail": ("mail", "full", False), "02-mail-dark": ("mail", "full", True),
    "03-calendar": ("calendar", "full", False), "04-calendar-dark": ("calendar", "full", True),
    "05-people": ("people", "zoom", False), "06-todo": ("todo", "zoom", False),
    "07-themes": ("themes", "fan", False), "08-features": ("features", "top", False),
}
MAC_THEMES = ("Blossom", "Coastline", "Garden")
MAC = False
BASE = ""
LAYOUT = "top"
SET = "light"
LANG = "en-us"
T = {}
DARK = False

SCRIPT = """
const pick = new URLSearchParams(location.search).get('s');
document.querySelectorAll('.scene').forEach(s => { if (pick && s.id !== pick) s.remove(); });
if (pick) document.documentElement.classList.add('single');
document.querySelectorAll('[data-shot]').forEach(w => {
  const img = new Image();
  img.className = 'shot';
  img.onload = () => { w.classList.add('has-shot'); w.prepend(img); };
  img.src = 'screens/' + w.dataset.shot + '.png';
});
"""


def section(key):
    global BASE, DARK, LAYOUT
    BASE, LAYOUT, variant = (SCENES_MAC if MAC else SCENES)[key]
    DARK = (SET == "dark") != variant
    _, glyph, c1, c2, c3, d1, d2, fn = META[BASE]
    style = f"--c1:{c1};--c2:{c2};--c3:{c3};--d1:{d1};--d2:{d2}"
    return (f'<section class="scene lay-{LAYOUT}{" dark" if DARK else ""}" id="{key}" style="{style}">'
            f'<div class="mesh"></div>{ico(glyph, cls="wm")}{fn()}</section>')


def build(lang, theme):
    global LANG, T, SET
    LANG, SET = lang, theme
    T = json.loads((HERE / "i18n" / f"{lang}.json").read_text(encoding="utf-8"))["scenes"]
    css = (HERE / "scenes.css").read_text(encoding="utf-8")
    if MAC:
        css += (HERE / "scenes.macos.css").read_text(encoding="utf-8")
    sections = "".join(section(k) for k in (SCENES_MAC if MAC else SCENES))
    html = f'<!DOCTYPE html><html lang="{lang}"{' class="mac"' if MAC else ''}><head><meta charset="utf-8"><title>Wino Store listing</title><style>{css}</style></head><body>{sections}<script>{SCRIPT}</script></body></html>'
    page = HERE / f"scenes.{'macos' if MAC else theme}.{lang}.html"
    page.write_text(html, encoding="utf-8")
    return page


def render(page, lang, theme, names):
    out = HERE / "out" if lang == "en-us" else HERE / "out" / lang
    if MAC:
        out = HERE / "out" / "macos" / (lang if lang != "en-us" else "")
    elif theme != "light":
        out = out / theme
    out.mkdir(parents=True, exist_ok=True)
    profile = Path(tempfile.gettempdir()) / "wino-store-listing-edge"
    for name in names:
        target = out / f"{name}.png"
        scale, height = ("1.5", 1200) if MAC else ("2", 1080)
        target.unlink(missing_ok=True)
        subprocess.run([
            str(EDGE), "--headless=new", "--disable-gpu", "--hide-scrollbars", f"--user-data-dir={profile}",
            "--allow-file-access-from-files", f"--force-device-scale-factor={scale}", f"--window-size=1920,{height + 120}",
            "--virtual-time-budget=6000", f"--screenshot={target}", f"{page.as_uri()}?s={name}",
        ], check=True, capture_output=True)
        # Edge can return before the file is written.
        for _ in range(100):
            if target.exists() and target.stat().st_size > 0:
                break
            time.sleep(0.3)
        time.sleep(0.5)
        # Headless Edge leaves a blank strip at the bottom of some scenes, so capture taller and crop to the canvas.
        with Image.open(target) as shot:
            size = (int(1920 * float(scale)), int(height * float(scale)))
            shot.crop((0, 0) + size).convert("RGB").save(target)
        print(target)


# Promotional art: name -> (width, height, body). Rendered at 2x: 3840x2160, 2160x2160 and 1440x2160.
def art_scenes():
    modes = "".join(app(n) for n in ("Calendar", "People", "ToDo"))
    return {
        "hero-16x9": (1920, 1080, f'<div class="art-hero">{app("Mail")}<div class="art-orbit">{modes}</div></div>'),
        "box-1x1": (1080, 1080, f'<div class="art-stack">{app("Mail")}<strong>Wino Mail</strong></div>'),
        "poster-2x3": (720, 1080, f'<div class="art-stack tall">{app("Mail")}<strong>Wino Mail</strong><div class="art-row">{modes}</div></div>'),
    }


def render_art():
    """The Store's promotional images, in the Mail scene's colors, without the all-modes icon."""
    _, _, c1, c2, c3, d1, d2, _ = META["mail"]
    css = (HERE / "scenes.css").read_text(encoding="utf-8")
    out = HERE / "out" / "art"
    out.mkdir(parents=True, exist_ok=True)
    profile = Path(tempfile.gettempdir()) / "wino-store-listing-edge"
    for name, (width, height, body) in art_scenes().items():
        size = f"width:{width}px;height:{height}px"
        html = (f'<!DOCTYPE html><html><head><meta charset="utf-8"><style>{css} html, body {{ {size}; overflow: hidden; }}</style></head>'
                f'<body><section class="scene art" style="{size};margin:0;--c1:{c1};--c2:{c2};--c3:{c3};--d1:{d1};--d2:{d2}">'
                f'<div class="mesh"></div>{body}</section></body></html>')
        page = HERE / f"art.{name}.html"
        page.write_text(html, encoding="utf-8")
        target = out / f"{name}.png"
        subprocess.run([
            str(EDGE), "--headless=new", "--disable-gpu", "--hide-scrollbars", f"--user-data-dir={profile}", "--allow-file-access-from-files",
            "--force-device-scale-factor=2", f"--window-size={width},{height + 120}", "--virtual-time-budget=4000", f"--screenshot={target}", page.as_uri(),
        ], check=True, capture_output=True)
        with Image.open(target) as shot:
            shot.crop((0, 0, width * 2, height * 2)).save(target)
        print(target)


if __name__ == "__main__":
    args = sys.argv[1:]

    def option(flag, default):
        if flag not in args:
            return default
        at = args.index(flag)
        value = args[at + 1]
        del args[at:at + 2]
        return value

    if "--art" in args:
        render_art()
        sys.exit()
    if "--mac" in args:
        args.remove("--mac")
        MAC = True
    lang, theme = option("--lang", "en-us"), option("--set", "light")
    langs = sorted(f.stem for f in (HERE / "i18n").glob("*.json")) if lang == "all" else [lang]
    for each in langs:
        for one in (["dark", "light"] if theme == "both" else [theme]):
            page = build(each, one)
            render(page, each, one, [k for k in (SCENES_MAC if MAC else SCENES) if not args or any(w in k for w in args)])
