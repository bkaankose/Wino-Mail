# Mail intelligence

Intelligence results are **device-local derived data**. The server analyzes mail the app sends
it and hands back artifacts; it stores no mail content and keeps no index. A second device
reprocesses its own mail rather than inheriting results from this one.

## The two stages

Every selected message goes to **Jev**, which returns smart labels, a priority and a single
decision about whether the message belongs in the daily briefing. Only the included messages
go on to **Luna**, which writes a headline and a one-line summary.

Jev cannot generate text, cannot order dates and cannot do arithmetic. Nothing in the app may
ask it for a due date, a count or a multi-step conclusion — that is why the briefing shows
labels, priority, headline and summary and nothing else. If a feature needs more than that,
it needs application code, not a better question.

## Job lifecycle

`MailIntelligenceCoordinator` owns it:

1. Select messages and resolve their bodies, preferring the local MIME cache.
2. Build **one encrypted SQLite file per job**, capped at `MaxMessagesPerJob` (1,000). A larger
   selection is split, so several jobs for one mailbox can be in flight at once.
3. Upload, and **persist the returned job id before anything else**. A job that is not recorded
   locally is a job whose results are unreachable after a restart.
4. Poll while active; resume polling on the next launch from the persistent job registry.
5. Import each stage in its **own transaction**, and acknowledge that stage only after its
   import commits.

Luna publishes an **empty stage** when no message qualified, so exactly two stages are always
acknowledged per job.

The mailbox id comes from the existing account/mailbox sync (`UserMailboxSyncEntry.Id`). If
that sync has not run, job submission fails with a clear error rather than inventing an id.

## Local store

`WinoMailIntelligence.db` is a separate SQLite file from the mail database. Rows are keyed by
**account + remote message id + content hash**.

The hash is the freshness key. An arriving artifact whose hash no longer matches the local
content is **dropped, not imported** — the message changed after it was submitted. Re-importing
the same identity and hash is idempotent and keeps the original arrival time, so a duplicate
result never appears as new.

There are no embeddings and no mail bodies in this database.

## Briefing

The briefing shows only Jev-included messages, grouped by the day they arrived. "New" is
first-import time. Ignoring a card keys on identity plus content hash, so a message that is
later reprocessed can resurface.

## Wiring

Every DTO crossing the API boundary must be registered in `WinoAccountApiJsonContext`.
`Wino.Services` is trimmable with the analyzer on and `tests/Wino.Services.AotSmoke` publishes
it with `PublishAot`, so an unregistered type fails the publish rather than failing at runtime.

Automatic-sync triggers swallow their exceptions on purpose. Intelligence must never break mail
synchronization.

## Removed

Semantic search, suggested replies and find-similar are gone. All three depended on embeddings
or server-side retrieval and have no replacement in this design.
