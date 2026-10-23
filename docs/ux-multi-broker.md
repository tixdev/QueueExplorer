# UX: one explorer, several brokers

Status: **implemented for IBM MQ browsing** (overview, queue list, queue detail, message viewer); operations are still
RabbitMQ-only. The reference for adding further brokers (Kafka, MSMQ). It describes what the user sees;
implementation details live in the code and the README.

## Principles

1. **One mental model, broker-native words.** The structure of every screen is the same for every broker (overview →
   list → detail → message), but labels use each broker's own vocabulary. An IBM MQ user must never read "vhost" or
   "consumers"; a RabbitMQ user must never read "queue manager" or "IPPROCS".
2. **The broker is always visible.** Every connection, staging batch and message carries a small broker badge
   (RabbitMQ / IBM MQ / Kafka). Colour is never the only cue: badge text is always present.
3. **Show what matters for that broker first.** RabbitMQ: consumers and delivery limits. IBM MQ: depth against max depth,
   open input/output counts, inhibited get/put, dead-letter queue, transmission queues.
4. **Reading is safe; changing is explicit.** IBM MQ browsing is non-destructive and says so; anything that removes or
   republishes messages needs a confirmation that names the queue and the number of messages.
5. **Missing permissions degrade, never break.** What the service account cannot read is listed under "Limited
   visibility" with the exact authority that is missing; everything else keeps working.
6. **Compact and quiet.** Dense tables, colour only where it carries meaning (status, error queues, near-full queues),
   status always icon + text.

## Connections

- The cluster picker becomes a **connection picker**: one entry per RabbitMQ cluster or IBM MQ queue manager, grouped by
  broker, each with its badge and description ("PROD · QMPAY1 · mq-pay-01(1414)").
- The URL keeps the same shape (`/c/<connection>/…`), so links and permissions work the same way.
- RBAC: for IBM MQ the *vhost* pattern of a grant matches the **queue manager name** and the queue pattern the queue
  name, so the same roles cover both brokers.

## Navigation per broker

| RabbitMQ | IBM MQ | Notes |
|---|---|---|
| Overview | Overview | same layout, broker-specific tiles |
| Queues | Queues | same list component, broker-specific columns |
| Exchanges | — (later: Topics) | hidden for IBM MQ |
| Shovels | — (later: Channels) | hidden for IBM MQ; channel status is a natural next screen |
| Staging, Audit | Staging, Audit | global, shared by all brokers |

## Vocabulary

| concept | RabbitMQ | IBM MQ |
|---|---|---|
| container | vhost | queue manager |
| messages in queue | messages (ready / unacked) | current depth (uncommitted) |
| capacity | — | max depth, shown as a fill meter |
| readers | consumers | open input count (IPPROCS) |
| writers | — | open output count (OPPROCS) |
| queue kinds | classic / quorum / stream | local / alias → target / remote → queue@qmgr via xmitq / model / transmission |
| failed messages | `error` queue (NServiceBus) | dead-letter queue of the queue manager (DEADQ) |
| browse | get + requeue (side effects) | **true browse, no side effects** |

## IBM MQ screens

### Overview
- KPI tiles: total depth · messages on the **dead-letter queue** · queues **≥ 80 % of max depth** · queues with depth
  and **no open input** (nobody reading) · transmission queues with depth (channel likely down) · queue manager
  (name, version, command level).
- Health: queue full (depth = max depth), get or put inhibited on a queue with traffic, DLQ not empty, transmission
  queue backlog, plus "Limited visibility" items.
- "Queues with most messages" and the per-queue-manager summary reuse the RabbitMQ components.

### Queue list
Columns: queue · type (with target for alias/remote) · depth · **depth meter vs max depth** · open input · open output ·
flags (GET/PUT inhibited, XMITQ) · last put / last get (relative). Filters: text, type, "only with messages",
"hide SYSTEM.*" (on by default).

### Queue detail
- Header: name, type, description; alias → target queue link; remote → `RNAME@RQMNAME via XMITQ`.
- Stats: depth/max depth meter, open input/output, uncommitted, oldest message age (when queue monitoring is on),
  last put/get, inhibited flags.
- Browse: same split view as RabbitMQ (list + detail), with a positive note: "Browsing does not change the queue".

### Message detail (IBM MQ tab, first tab for IBM MQ messages)
- **Header chain** as a row of chips in transmission order, e.g. `MQDEAD → MQHRF2 → MQSTR`; clicking a chip scrolls to
  that header.
- **MQMD**: format, CCSID with charset name (e.g. `500 · EBCDIC International`), encoding, persistence, priority,
  message type, put application and time, reply-to queue@qmgr, backout count, expiry; MsgId/CorrelId in hex with a
  printable-text hint, copyable.
- **Dead-letter header**: reason as code + constant + plain-language explanation (e.g. `2053 MQRC_Q_FULL — the
  destination queue was full`), original destination `queue@qmgr`, put application/time of the DLH.
- **RFH2**: one card per folder — `mcd` (message domain/type), `jms` (destination, timestamp, correlation id,
  delivery mode, expiration, priority), `usr` (application properties as a table), others as raw XML.
- Body tab: decoded with the CCSID of the last header (JSON/XML pretty-printed and syntax-coloured as today), a line
  "decoded from CCSID 500 (EBCDIC)" and a toggle to **hex view** for binary or suspicious decoding. PCF bodies are
  shown as a parameter table.

## Permissions and resilience (IBM MQ)

| missing authority | what still works | what the user sees |
|---|---|---|
| put on `SYSTEM.ADMIN.COMMAND.QUEUE` / model queue (no PCF) | configured queue list, per-queue depth via MQINQ | "Limited visibility: queue list from configuration — PCF commands not authorised" |
| `+dsp` for queue status | depths and definitions | "Open handles and last put/get not available" |
| `+inq` on a queue | everything else | depth shown as "–" for that queue |
| `+browse` on a queue | everything else | browse button disabled with "not authorised to browse (MQRC 2035)" |
| connection refused (2035/2059/2393 TLS) | other connections | connection marked unavailable with the MQRC name and meaning |

Errors always show the **MQRC number, constant name and a one-line meaning**, never only a number.

## Consistency rules

- Same components across brokers: KPI tile, health list, list table with virtual scroll, message list, message viewer,
  staging. Broker-specific content goes in dedicated sections/tabs, never as conditionals scattered in shared views.
- Operations (move, copy, delete, export, staging) appear for IBM MQ only when implemented for it; until then the
  buttons are not shown (no disabled clutter).
- Relative times with absolute time on hover; numbers en-GB; monospace for names and ids.
