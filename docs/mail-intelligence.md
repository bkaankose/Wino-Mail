# Mail intelligence

Intelligence results are **device-local derived data**. The server analyzes mail the app sends
it and hands back artifacts; it stores no mail content and keeps no index. A second device
reprocesses its own mail rather than inheriting results from this one.

## The two phases

Every selected message goes through **Classification**, which returns smart labels, a priority
and a single decision about whether the message belongs in the daily briefing. Only the included
messages go on to **Enrichment**, which writes a headline and a one-line summary. Classification
runs on the Jev model and Enrichment on Luna; the model names appear only where a model is
configured, never in phase, stage or type names.

Jev cannot generate text, cannot order dates and cannot do arithmetic. Nothing in the app may
ask it for a due date, a count or a multi-step conclusion — that is why the briefing shows
labels, priority, headline and summary and nothing else. If a feature needs more than that,
it needs application code, not a better question.

## Runs and streamed batches

The server stores nothing for intelligence, not even encrypted. `MailIntelligenceCoordinator` sends
work on the streamed route (`POST /api/v3/ai/intelligence/mailboxes/{mailboxId}/messages:stream`,
contracts in `MailIntelligenceStreamContracts`) and keeps a run's state in memory only:

1. Select messages, skip those already processed at their current hash, and resolve their bodies,
   preferring the local MIME cache.
2. Split the selection into batches of at most `MailIntelligenceStreamLimits.MaxMessagesPerBatch`
   (50) messages and `MaxRequestBytes` (24 MiB of request JSON, base64 envelopes included).
3. Encrypt a batch only once one of the **two app-wide stream slots** is free (the server allows two
   streams per user and answers a third with 429, which the client waits out per `Retry-After`).
   Envelopes are bound to the request route and must be at most five minutes old.
4. Read the NDJSON response as it arrives. Every `result` frame must carry the next sequence number
   and decrypt with the device result key under its frame route; each item is imported in its own
   transaction the moment it arrives, classification first, enrichment later. `done` ends the batch
   and must count the result frames received; `error` ends it early.
5. A failure item with `AI_QUOTA_EXCEEDED` means the batch was only partly paid: the run sends no
   further batches and shows the quota pause.

Cancelling a run closes its requests, which stops the model calls on the server and refunds what was
not delivered. A batch that ends early, drops its connection or is refused leaves the rest of its
messages without artifacts, so the next run selects them again. There is nothing to poll,
acknowledge, resume or expire.

Builds before the streamed route recorded v2 jobs in a `MailIntelligenceJob` table. On upgrade the
coordinator deletes those jobs on the server (best effort, the only remaining v2 job call) and drops
the table with its last row; their messages are selected again and go through the streamed route.

The mailbox id comes from the existing account/mailbox sync (`UserMailboxSyncEntry.Id`). Only a
backup that includes accounts writes that list, so when the mailbox is missing the first submission
adds it: the stored entries go back unchanged and the mailbox is added with its identity only
(address, provider, name, colour, order), without server settings or preferences. If the server
still has no id after that, submission fails with a clear error rather than inventing one.

## Local store

`WinoMailIntelligence.db` is a separate SQLite file from the mail database. Rows are keyed by
**account + remote message id + content hash**.

The hash is the freshness key. An arriving artifact whose hash no longer matches the local
content is **dropped, not imported** — the message changed after it was submitted. Re-importing
the same identity and hash is idempotent and keeps the original arrival time, so a duplicate
result never appears as new.

There are no embeddings and no mail bodies in this database.

## Briefing

The briefing shows only messages Classification included, grouped by the day they arrived. "New" is
first-import time. Ignoring a card keys on identity plus content hash, so a message that is
later reprocessed can resurface.

The shell's briefing badge means "today's page has a card you have not seen": it is computed from
the same facts the panel shows for today, so classifying older mail, ignored cards, hidden accounts
and removed mail never raise it.

## Wiring

Every DTO crossing the API boundary must be registered in `WinoAccountApiJsonContext`, or for the
streamed route in `MailIntelligenceStreamJsonContext` (web defaults, like the server).
`Wino.Services` is trimmable with the analyzer on, and the Windows Release gate publishes it with
Native AOT, so an unregistered type fails the build rather than failing at runtime.

Automatic-sync triggers swallow their exceptions on purpose. Intelligence must never break mail
synchronization.

## Removed

Semantic search, suggested replies and find-similar are gone. All three depended on embeddings
or server-side retrieval and have no replacement in this design.
