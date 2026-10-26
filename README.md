# DynaQueueExplorer

A web-based explorer for **RabbitMQ**, **IBM MQ** and **MSMQ**, built for teams running **NServiceBus**. It replaces desktop tools such as
QueueExplorer (Windows-only, slow on large brokers) with a shared, fast web application that:

- shows the state of one or more RabbitMQ clusters — nodes, queues, exchanges, bindings, shovels — and IBM MQ queue
  managers — queues, depths, open handles, dead-letter and transmission queues — and MSMQ machines — private,
  dead-letter and outgoing queues — without requiring users to be broker administrators;
- recognises NServiceBus messages and displays them in a structured way (message types, endpoints, failure details);
- moves, copies, retries, exports and imports messages safely;
- parks messages in a **staging area**, shared across brokers, to inspect and edit them before deciding where to
  send them;
- applies its own **role-based access control** on top of a single privileged service account, and records every
  sensitive operation in an **audit log**.

IBM MQ and MSMQ are supported read-only for now (overview, queues, browse with full decoding); moving, copying and
staging their messages, and Kafka, are planned. Each broker plugs in next to the others: see [docs/ux-multi-broker.md](docs/ux-multi-broker.md).

---

## What you can do

### Explore clusters
- **Overview** — a compact operational dashboard:
  - KPI tiles: messages (with sparkline), messages in error queues (with sparkline), publish/deliver rates, stopped
    endpoints, queues, connections;
  - **Health**: nodes down, memory/disk alarms, memory or file descriptors above 80%, queues not running, shovels not
    running, endpoints with messages and no consumers, messages in error queues — each with a status icon and a link;
  - **Trend** of all messages and of messages in error queues over 1 h / 6 h / 24 h (two small multiples sharing one
    time axis, crosshair tooltip, keyboard navigation, table view). Samples are taken every minute, also while nobody
    uses the explorer, and kept in memory for 24 h;
  - **Failures**: on request ("Analyse"), the NServiceBus error queues are read (headers only) and grouped by failing
    endpoint and exception type, with counts, last failure and a sample message. Queues whose delivery limit could
    dead-letter messages are skipped; results are cached and shared by every authorised user;
  - **Queues with most messages**, **stopped endpoints** (messages waiting, no consumers) and a **per-vhost summary**
    (queues, messages, messages in error queues, stopped endpoints);
  - node resources in one line per node.
- **Node resources**: memory, free disk (against its alarm threshold), file descriptors, Erlang processes, with alarm
  highlighting. Metrics a RabbitMQ version does not report are hidden.
- **Queues**: every queue of every visible vhost in one virtualised list — depth, ready/unacked, consumers (queues
  without consumers are highlighted, i.e. an endpoint that is down), type, state. Filter by vhost and text, sort by
  name/messages/consumers, hide the NServiceBus delayed-delivery infrastructure (`nsb.v2.delay-*`).
- **Queue detail**: live message rates, the effective **delivery limit** (quorum queues), policy, arguments and the
  bindings that route into the queue.
- **Exchanges**: every exchange classified for the NServiceBus conventional routing topology — **events** (`Namespace:Type`
  exchanges), **endpoints** (exchange bound to the queue of the same name), delayed-delivery infrastructure, built-in —
  with a filter per kind. For an event, a **flow diagram** shows its subscribing endpoints (directly or through base
  types/interfaces, e.g. "via IOrderEvent") with their queue depth and consumers; for an endpoint, the events it is
  subscribed to. Raw bindings stay available in both directions.
- **Shovels**: status of static and dynamic shovels merged with their definitions, so a shovel that cannot connect
  (and therefore has no status in RabbitMQ) is still listed. Credentials in shovel URIs are masked.

### Read messages
- **Browse** a queue (up to 200 messages at a time) and filter the result by message type, exception, id or content.
- **NServiceBus-aware viewer**, recognised from the `NServiceBus.*` headers:
  - message types parsed from `EnclosedMessageTypes` (namespace, assembly, version, generics, nested types);
  - intent, originating endpoint and machine, time sent, message/conversation/correlation ids, reply-to, saga info;
  - for failed messages: exception type and message, **failing handler or saga**, inner exception, failed queue,
    endpoint and machine, time of failure, delayed retries, application data attached to the exception
    (`Exception.Data`) and the stack trace;
  - all headers grouped (failure, exception data, saga, diagnostics, ServiceControl, NServiceBus, other).
- **Body viewer**: JSON and XML are pretty-printed and **syntax-coloured** (keys, strings, numbers, literals; tags,
  attributes, values), binary payloads get a hex dump; copy to clipboard.
- The message list shows when each message was **produced**, as date and time in the browser's time zone (relative
  time on hover): NServiceBus `TimeSent`, else the AMQP `timestamp` property, else the IBM MQ put date/time.

### IBM MQ
- The connection picker groups connections by broker and a badge always shows which broker is open. For IBM MQ the
  *vhost* of the URL and of RBAC grants is the **queue manager name**; Exchanges and Shovels are not shown.
- **Overview**: total depth, messages on the **dead-letter queue**, queues at ≥ 80 % of max depth, queues with messages
  and **no open input handle** (nobody reading), **transmission queues with a backlog** (sender channel probably down),
  queue manager version, command level and platform. Health lists full queues, get/put inhibited queues in use, the DLQ
  and the limits of the service account.
- **Queues**: type (alias → target, remote → `queue@qmgr via xmitq`, transmission, model), depth with a **fill meter**
  against max depth, open input/output counts, GET/PUT inhibited flags, last put / last get; `SYSTEM.*` hidden by
  default. The explorer's own PCF reply queues (`DQE.PCF.*`) are never listed.
- **Browse is a real MQ browse**: it does not change the queue, so no confirmation is needed.
- **Message viewer**, *IBM MQ* tab: the **header chain** (e.g. `MQDEAD → MQHRF2 → MQSTR`), the MQMD (format, CCSID
  with its code page, encoding, persistence, put application and time, reply-to, backout count, expiry), MsgId and
  CorrelId in hex with their printable text, the **dead-letter header** (reason as code, constant and meaning,
  original destination), the transmission queue header, **RFH2 folders** (`mcd`, `jms`, `usr` properties…) and **PCF**
  messages as a parameter table. The body is decoded with the CCSID of the last header — UTF-8, Latin-1/Windows code
  pages and **EBCDIC** (500, 1140–1149 with euro, 037, 273, 280…) — and can be switched to a **hex view** to check a
  doubtful decoding.

### MSMQ
- MSMQ is read on Windows by the **MSMQ agent** (`src/agent`), a small Windows service that uses the native MSMQ API
  and answers the explorer over HTTP(S) with a shared key. One agent can read several machines remotely (as
  QueueExplorer does from a workstation) or run on the MSMQ server itself, which is faster. Each **machine is a
  connection**; it plays the role of the vhost in URLs and RBAC grants.
- **Overview**: messages in the private queues, the **dead-letter queues** (transactional and non-transactional),
  NServiceBus **error queues** with messages, the **outgoing queues** with messages and their connection state (messages
  waiting for a machine that is not connected are a health item), the queues with most messages, and how the depths
  were read.
- **Queues**: private queues, the two system dead-letter queues and the outgoing queues towards other machines, with
  messages, size and journal; NServiceBus infrastructure (`.retries`, `.timeouts`, `.distributor.*`…) hidden by default.
- **Peek is non-destructive**: messages are read with a cursor. Outgoing queues cannot be peeked.
- **Message viewer**, *MSMQ* tab: label, ids, **class** — for a dead-lettered message, *why* it was dead-lettered
  (e.g. `0xC001 · NACK: purged from the destination queue`) — sent/arrived times, response, admin and destination
  queues, transaction, time-to-reach/time-to-be-received, sender. **NServiceBus headers** are read from the message
  *extension* (XML), so the NServiceBus view, failure details and Header tab work as for RabbitMQ.

### Migration flows (MSMQ ⇄ RabbitMQ)
While endpoints move from MSMQ to RabbitMQ, messages travel through **RabbitMQ shovels** and the **bridge** that moves
messages between MSMQ and RabbitMQ. The *Flows* page shows every such path as a pipeline from source to destination,
with the messages waiting at each step:
- **shovels**: source and destination queue or exchange (any vhost, or another broker by URI) and the shovel state; for
  an exchange destination, the bindings it routes through (two levels);
- **bridge**: a queue in a configured *bridge vhost* with the same name as a queue on one of the environment's MSMQ
  machines (`MigrationBridges`). The direction comes from the shovels around the RabbitMQ queue — fed by a shovel it
  sends to MSMQ, drained by one it receives from MSMQ; otherwise it is guessed from the name (`torabbit`) or shown as
  unknown;
- connected steps form one flow, e.g. `dx` queue → shovel → exchange → binding → `internal-services` queue, or MSMQ
  queue → bridge → bridge-vhost queue → shovel → `internal-services` queue;
- problems: shovels not running, messages waiting with no consumer, messages in error queues, shovel ends that do not
  exist. Flows can be filtered by direction, bridge (environment), text and the selected queue group.

Queue pages (RabbitMQ and MSMQ) and the exchange panel say when the queue or exchange is part of a flow and link to it.

### Move, copy, retry
All operations work on the **selected messages** or, if none is selected, on the **first N messages** of the queue.
- **Move** / **Copy** to any queue (any vhost of the cluster).
- **Retry** failed NServiceBus messages: each message goes back to the queue in its `NServiceBus.FailedQ` header (as
  ServiceControl does), optionally removing the failure headers. Before confirming, the dialog shows where the
  messages will go, grouped by destination queue, and how many have no `FailedQ` (they stay where they are).
- A message is removed from its source **only after RabbitMQ has confirmed it at the destination** (publisher
  confirms, mandatory routing). Unroutable or rejected messages stay in the source queue and are reported.
- Broker-managed headers (`x-delivery-count`, `x-death`, `x-first-death-*`, `x-last-death-*`) are dropped on
  republishing, since they describe the history of the original copy.
- **Delete** the selected messages (or the first N), after a confirmation.
- **Purge** a queue: removes every ready message; the queue name must be typed to confirm (checked by the server too).

### Files and staging area
- **Save to file**: export messages to a `.mqx` file — a zip with one JSON document per message (routing, properties,
  headers with their AMQP types) and the **exact body bytes**. Exporting leaves the messages in the queue.
- **Staging area** (stored in SQL Server, shared by the team and by all brokers):
  - **import** a `.mqx` file, or **copy/move** messages from a queue into a staging batch;
  - every batch and message records its **broker** (RabbitMQ today; IBM MQ, Kafka next): the batch list shows it and
    can be filtered by broker;
  - inspect them with the same viewer, **discard** or restore some of them;
  - **edit** a message (RabbitMQ): body (with JSON formatting/validation), AMQP properties (content type, ids, type,
    reply-to, expiration, delivery mode, priority…) and headers (change, add, remove). Untouched headers keep their
    exact AMQP type and changed ones keep it when the new value fits (an integer stays an integer, binary values are
    edited as base64). The original is kept on the first edit and can be **restored**; edits are audited and the
    message shows who changed it and when;
  - **publish** (selected or all non-discarded messages) to any queue, or back to each message's `FailedQ`
    (with the same per-destination preview); publishing is only offered to a broker of the same kind;
    every message remembers where and when it was last published;
  - **export** a batch to `.mqx` or delete it.

### Security and audit
- **RBAC**: a grant is an *action* on resources matching *cluster / vhost / queue* glob patterns (`*`, `?`); roles
  group grants and are assigned to users or groups. Everything is denied unless granted, and checks are enforced only
  on the server. Clusters a user cannot access at all answer 404, not 403.
- **Actions**: `overview.read`, `nodes.read` (cluster), `exchanges.read`, `shovels.read` (vhost), `queues.read`,
  `messages.browse`, `messages.move`, `messages.publish`, `messages.export`, `messages.delete`, `queues.purge` (queue),
  `staging.read`, `staging.write`, `audit.read` (global), `*` (everything). Move requires `messages.move` on the source and
  `messages.publish` on the destination.
- **Audit log** of logins, browses, moves, copies, retries, exports, staging imports/edits/publishes, including denied
  attempts, with keyset-paginated viewer.
- Session cookie (`HttpOnly`, `SameSite=Strict`), anti-CSRF header (`X-DQE-Client`) on state-changing calls, login
  rate limiting. RabbitMQ credentials never leave the server.
- **TLS** towards RabbitMQ (`https://` management API, `amqps://` AMQP). Server certificates are always validated —
  against the machine's trust store or, for an internal CA, against the CA configured for the cluster
  (`CaCertificatePath`); validation is never switched off.

---

## How it stays fast

- **Snapshots, not per-request calls.** A background loop per cluster keeps an in-memory snapshot built from parallel
  bulk calls. List views never call RabbitMQ, so broker load does not grow with users.
- **Two refresh tiers.** The *state* — `queues` (with `disable_stats`), `consumers`, `overview`, `nodes`, `shovels` —
  is refreshed every 5 s; the *structure* that changes mostly with deployments — `vhosts`, `exchanges`, `bindings`,
  `parameters/shovel` — every 60 s, or immediately with the refresh button. New queues therefore show up within 5 s;
  new exchanges and bindings within a minute. A state-only cycle costs ~45 ms of broker time, a full one ~65 ms
  (local measurements, ~50 KB gzip).
- **Polling only while the cluster is in use.** After 2 minutes without requests the loop stops (configurable to slow
  polling instead); the next request waits for a fresh snapshot (~70 ms) and polling resumes. A stale snapshot is always
  refreshed before answering, and concurrent refreshes are coalesced. All intervals can be overridden per cluster.
- **Bounded connections**: one pooled HTTP client per cluster (max 4 connections). AMQP is used only for message
  operations, with one long-lived connection per cluster and vhost, closed after 5 idle minutes. Messages are read with
  a prefetching consumer (one round-trip for the whole batch, not one `basic.get` per message) and published in
  concurrent confirmed batches.
- **Cheap revalidation**: snapshot versions are hashes of the raw payloads; responses carry
  `ETag = version + permission fingerprint`, so unchanged data is a body-less 304 and users with the same
  permissions share the serialized payload. Responses are Brotli/gzip compressed.
- **Frontend**: polls only while the tab is visible, skips unchanged ETags, builds binding indexes once per snapshot,
  virtualises long lists; ~82 KB initial transfer.

Measured locally (622 queues, 1567 exchanges, 4174 bindings): snapshot refresh ~75 ms; full topology 1.0 MB raw →
40 KB compressed, served in ~4 ms; revalidation 304 in < 1 ms.

## Things to know about RabbitMQ

- **Browsing is not side-effect free.** RabbitMQ has no non-destructive browse for classic/quorum queues: messages are
  fetched and requeued (flagged `redelivered`, order may change; requeued quorum messages go to the back).
- On **quorum queues** (the NServiceBus default) each browse increments `x-delivery-count`. RabbitMQ 4 applies a
  default **delivery limit of 20**: reaching it dead-letters or drops the message. The explorer reads the effective
  limit live before browsing and asks for confirmation when it is low.
- Set the limit to unlimited (`-1`) with a policy on the queues you browse. NServiceBus endpoint input queues already
  get `nsb.<queue>.delivery-limit` = 100000 from the transport, and **NServiceBus refuses to start an endpoint whose
  input queue has a different limit from another policy** — so do not use a catch-all `.*` policy. The local
  environment applies `delivery-limit: -1` to `^(error|audit)$` only. A queue gets a single policy (the one with the
  highest priority), so check existing policies before adding one in production.
- A dynamic shovel can declare its source/destination queue (as a classic queue) if it starts before the queue exists;
  use `src-predeclared`/`dest-predeclared`.

---

## Things to know about IBM MQ

- The explorer connects as an MQ client (TCP, optionally TLS) and lists queues with **PCF commands**, reading every
  queue in one exchange per refresh. Without PCF rights it falls back to the queues listed in `Clusters[].Queues`.
- **Last put / last get** and the oldest message age only exist when **queue monitoring** is on (`MONQ` on the queue
  manager or the queue). MQ reports those times in the queue manager's local time without naming the time zone: set
  `Clusters[].TimeZone` (IANA id, e.g. `Europe/Rome`) when it is not UTC.
- TLS with the IBM MQ .NET client 10.0.1: use an explicit **TLS 1.2 CipherSpec** (e.g.
  `ECDHE_RSA_AES_256_GCM_SHA384`); `ANY_TLS12_OR_HIGHER` and TLS 1.3 CipherSpecs do not work on macOS. With a
  certificate file the IBM client accepts any CA and does not check the host name, so the explorer first verifies the
  server certificate itself (against `CaCertificatePath` or the machine's trust store, host name included).

## Things to know about MSMQ

- MSMQ has no remote bulk API. The agent lists the private queues of a machine in one call (MSMQApplication), reads
  every depth in one call from the **`MSMQ Queue` performance counters**, and only asks the outgoing queues that hold
  messages for their state. When a machine's counters cannot be read remotely (Remote Registry stopped, "network path
  not found"), it falls back to one management call per **active** queue (MSMQ keeps every queue that holds messages
  active, so the others are empty): a few seconds per hundred queues. The overview says which method was used.
- The system dead-letter queues have neither counters nor management info: they are counted by peeking headers, up
  to `Agent:DeadLetterCountLimit` (shown as "≥ n").
- Reading a machine remotely takes from under a second to tens of seconds; the explorer therefore answers from its
  snapshot at once and refreshes it in the background every 30 s while the machine is in use (and once at startup).
- The agent's Windows account needs *Peek* (and *Get Properties*) on the queues, like the people who use QueueExplorer
  today; for remote counters, membership of *Performance Monitor Users* on the MSMQ machines.

## Architecture

```
src/server/
  DynaQueueExplorer.Core            RBAC model, cluster options, audit contract, message keys, body formatting
  DynaQueueExplorer.RabbitMq        management API client, snapshots, AMQP connections, move/copy/publish, .mqx format
  DynaQueueExplorer.IbmMq           IBM MQ client connections, PCF snapshots, browse, header chain and CCSID decoding
  DynaQueueExplorer.Msmq            MSMQ agent client, snapshots, NServiceBus headers from the extension
  DynaQueueExplorer.NServiceBus     NServiceBus message recognition and decoding
  DynaQueueExplorer.Infrastructure  EF Core / SQL Server: roles, grants, assignments, audit, staging
  DynaQueueExplorer.Api             ASP.NET Core minimal API (.NET 10); serves the SPA
src/agent/DynaQueueExplorer.MsmqAgent  Windows service reading MSMQ (COM API, mqrt.dll, performance counters)
src/web/                           Angular 22 (zoneless, signals, CDK virtual scroll); UI in English
tests/DynaQueueExplorer.Tests       xUnit, including fixtures captured from real NServiceBus 10 failures
tools/perf-env/                    generators for an NServiceBus-like RabbitMQ topology and sample messages
tools/NsbSimulator/                real NServiceBus 10 endpoints that fail in different ways
tools/IbmMqSeeder/                 fills the local IBM MQ queue manager with messages in every supported format
docker-compose.yml                 RabbitMQ 4.1 (management + shovel, TLS), SQL Server 2022, IBM MQ 9.4 (QM1, TLS)
```

NuGet versions are managed centrally in `Directory.Packages.props` (CPM, transitive pinning); common build settings
live in `Directory.Build.props` (warnings are errors).

## Getting started

```sh
tools/perf-env/generate_certs.sh                    # local CA + server certificate for RabbitMQ TLS (once)
python3 -I tools/perf-env/generate_definitions.py   # 10 vhosts × ~210 queues/exchanges, ~3.5k bindings, shovels, policies
docker compose up -d
python3 -I tools/perf-env/seed_messages.py          # ~2.4k NServiceBus-shaped messages (error, audit, endpoints)
dotnet run --project tools/IbmMqSeeder              # IBM MQ test messages: JSON, JMS/RFH2, EBCDIC, binary, DLQ, XMIT, PCF

cd src/web && npm ci && npx ng build && cd -        # builds the SPA into the API's wwwroot
dotnet run --project src/server/DynaQueueExplorer.Api --launch-profile http
# → http://localhost:5044
```

The IBM MQ image (`icr.io/ibm-messaging/mq`, amd64) runs emulated on Apple silicon and takes a minute to start. Its
setup is in `docker/ibmmq/20-dqe.mqsc`: TLS on `DEV.APP.SVRCONN`, queue monitoring on, test queues, and
`RESTRICTED.QUEUE`, which the `app` account deliberately cannot see. The console is at https://localhost:9443.

For frontend development run the API as above and `npx ng serve` in `src/web` (proxy to :5044) → http://localhost:4200.
The RabbitMQ console is at https://localhost:15671 (`dqe-admin` / `dqe-admin`; the certificate is signed by the local
CA in `docker/rabbitmq/certs/ca.pem`).

Like production, RabbitMQ listens with **TLS** on 5671 (AMQPS) and 15671 (management HTTPS), and the explorer uses only
those, validating the server against the local CA. The plain ports 5672/15672 stay open for the local tools (seed
script, NServiceBus simulator).

To rebuild the local environment from scratch (topology, policies, data):

```sh
python3 -I tools/perf-env/generate_definitions.py
docker compose down -v && docker compose up -d
python3 -I tools/perf-env/seed_messages.py
```

### Real NServiceBus failures

```sh
dotnet run --project tools/NsbSimulator -- --orders 200 --rate 10          # keeps consuming until Ctrl+C
dotnet run --project tools/NsbSimulator -- --orders 100 --exit-after 30    # stops on its own
```

Hosts `Simulation.Sales`, `Simulation.Billing`, `Simulation.Shipping` (saga) and a send-only client in the vhost
`simulation` (created on start, with the unlimited delivery-limit policy on `error`/`audit`). Recoverability is
2 immediate + 2 delayed retries through the real `nsb.v2.delay-*` infrastructure. About 40% of the orders fail:

| scenario          | where           | outcome                                                        |
|-------------------|-----------------|----------------------------------------------------------------|
| `Transient`       | Billing         | fails twice, succeeds on immediate retries                     |
| `PaymentDeclined` | Billing         | business exception with `Exception.Data` → `error`             |
| `GatewayTimeout`  | Billing         | `HttpRequestException` with inner `SocketException` → `error`  |
| `NullReference`   | Sales           | bug in the handler → `error`                                   |
| `SagaFailure`     | Shipping (saga) | exception in `ShippingPolicy` → `error`                        |
| `Poison`          | Billing         | truncated JSON → `MessageDeserializationException`, no retries |

Headers captured from these messages are used as test fixtures (`tests/.../Fixtures/nsb10-*.headers.json`).

## Configuration

Clusters are configured in the API settings (there is no UI for them yet). Each cluster needs the **management API**
URL (topology, browse) and the **AMQP** URL (move, copy, retry, export, staging publish), plus the credentials of a
privileged RabbitMQ service account used for both. Use `https://` and `amqps://` for TLS; set `CaCertificatePath`
(PEM or DER, relative to the API folder or absolute) when the broker's CA is not trusted by the machine:

```json
"Clusters": [
  {
    "Name": "prod",
    "Description": "Production RabbitMQ",
    "ManagementUrl": "https://rabbitmq-prod.example.com:15671",
    "AmqpUrl": "amqps://rabbitmq-prod.example.com:5671",
    "CaCertificatePath": "/etc/ssl/internal-ca.pem",
    "Username": "dqe-service",
    "Password": "<secret>"
  }
]
```

Settings are read, in increasing priority, from `appsettings.json`, `appsettings.{Environment}.json`,
**`appsettings.Local.json`** (git-ignored — put real credentials there; see `appsettings.Local.example.json`) and
environment variables (`Clusters__0__Password=…`). Configuration arrays are merged **by position**: a cluster at index 0
in `appsettings.Local.json` replaces the `local` development cluster, one at index 1 is added next to it.

IBM MQ queue managers go in the same list with `"Broker": "ibmmq"`:

```json
{
  "Name": "mq-prod",
  "Description": "Payments queue manager",
  "Broker": "ibmmq",
  "QueueManager": "QMPAY1",
  "Host": "mq-pay-01.example.com",
  "Port": 1414,
  "Channel": "DQE.SVRCONN",
  "CipherSpec": "ECDHE_RSA_AES_256_GCM_SHA384",
  "CaCertificatePath": "/etc/ssl/internal-ca.pem",
  "ClientCertificatePath": "/etc/dqe/client.p12",
  "ClientCertificatePassword": "<secret>",
  "TimeZone": "Europe/Rome",
  "Username": "dqesvc",
  "Password": "<secret>"
}
```

| setting | meaning |
|---|---|
| `ConnectionStrings:Explorer` | SQL Server database (roles, audit, staging) |
| `Clusters[]` | `Name`, `Description`, `ManagementUrl`, `AmqpUrl`, `CaCertificatePath`, `Username`, `Password` |
| `RabbitMq:ActiveRefreshInterval` | state refresh while the cluster is in use (5 s) |
| `RabbitMq:StructureRefreshInterval` | structure refresh: vhosts, exchanges, bindings, shovel definitions (60 s) |
| `RabbitMq:IdleAfter` | a cluster without requests for this long is idle (2 min) |
| `RabbitMq:IdleRefreshInterval` | polling while idle; unset (default) = no polling |
| `Clusters[].Refresh` | per-cluster overrides of the four settings above, e.g. `{ "ActiveRefreshInterval": "00:00:15" }` |
| `RabbitMq:MaxConnectionsPerCluster` | HTTP connections towards each management API (4) |
| `RabbitMq:AmqpIdleTimeout` | idle AMQP connections are closed after this (5 min) |
| `RabbitMq:MaxMessagesPerOperation` | upper bound of a move/copy/export/stage/delete (10 000) |
| `RabbitMq:TrendSampleInterval` | overview trend sampling (1 min; unset disables the trend) |
| `RabbitMq:TrendRetention` | trend history kept in memory (24 h) |
| `RabbitMq:ErrorAnalysisMaxMessages` | messages read per error queue by the failure analysis (1 000) |
| `RabbitMq:BrowseTruncateBytes` | payloads larger than this are truncated when browsing (256 KB) |
| `Clusters[]` (IBM MQ) | `Broker: ibmmq`, `QueueManager`, `Host`, `Port`, `Channel`, `CipherSpec`, `CaCertificatePath`, `ClientCertificatePath`/`Password` (mutual TLS), `TimeZone`, `Queues` (fallback list without PCF), `Username`, `Password` |
| `IbmMq:ActiveRefreshInterval` / `StructureRefreshInterval` / `IdleAfter` | as for RabbitMQ (5 s / 60 s / 2 min) |
| `IbmMq:PcfResponseTimeout` | wait for PCF responses (10 s) |
| `IbmMq:BrowseTruncateBytes` | message data read per message when browsing (256 KB) |
| `LocalUsers[]` | test-phase users (`Username`, `Password`, `DisplayName`, `Groups`) |
| `QueueGroups[]` | cross-broker queue groups (`Name`, `Description`, `Rules[]` of `Broker`, `Cluster`, `Vhost`, `Queue`), see below |

### Queue groups

A queue group is a named selection of queues that spans brokers, typically an environment: "T1 CH" is a set of RabbitMQ
vhosts on one side and a set of IBM MQ queues, recognised by their name, on the other. A queue belongs to a group when
**any** of its rules matches. A rule has glob patterns (`*`, `?`, case-insensitive) on `Cluster`, `Vhost` (for IBM MQ:
the queue manager) and `Queue`, plus an optional `Broker` (`rabbitmq`, `ibmmq`). A field that is not set matches
everything. For RabbitMQ the `Queue` pattern also selects the exchanges that have the same name.

```json
"QueueGroups": [
  {
    "Name": "T1 CH",
    "Description": "Test environment 1, Switzerland",
    "Rules": [
      { "Broker": "rabbitmq", "Vhost": "*-ch-*.t1.*" },
      { "Broker": "ibmmq", "Queue": "*CHT1.*" }
    ]
  }
]
```

In the UI the group is picked next to the connection, and it stays selected when you switch connection or come back
later, so going from RabbitMQ to IBM MQ keeps you in the same environment. It narrows every list:
- queues, exchanges and the vhost filters;
- the overview: messages, queues and exchanges KPIs, health, failures, stopped endpoints, the top queues and the
  per-vhost table. On RabbitMQ the **trend** is kept per group too. Rates, connections and nodes stay cluster-wide
  and are labelled that way.

The top bar shows how many queues of the connection are in the group, or warns when the group has no rule for that
broker. In the move/copy dialog the group's vhosts and queues come first, but any destination can still be chosen.
Detail pages opened from a link work even for queues outside the group. Groups only narrow what is shown: access is
still decided by RBAC.

### MSMQ machines and the agent

Each MSMQ machine is a connection with `Broker: msmq`, the machine name as in MSMQ format names (`Host`) and the
agent that reads it:

```json
{
  "Name": "msmq-ba-wxi001",
  "Description": "MSMQ T1 distributor",
  "Broker": "msmq",
  "Host": "msmq-ba-wxi001.moon.corner.tech",
  "AgentUrl": "http://localhost:5180",
  "AgentApiKey": "<the agent's Agent:ApiKey>"
}
```

The agent (`src/agent/DynaQueueExplorer.MsmqAgent`, .NET 10, Windows with MSMQ installed) is configured in its
`appsettings.json` / `appsettings.Local.json`:

| setting | meaning |
|---|---|
| `Urls` | where it listens (`http://localhost:5180`); use `https://…` with a Kestrel certificate when the explorer runs on another host |
| `Agent:ApiKey` | shared secret (≥ 24 characters) the explorer sends in `X-DQE-Agent-Key` |
| `Agent:Machines` | machines it may read; empty = only its own. Any other machine is refused |
| `Agent:DepthParallelism` | management calls in parallel when counters are not readable (16) |
| `Agent:DeadLetterCountLimit` | dead-letter messages counted at most (1000) |

```powershell
dotnet publish src/agent/DynaQueueExplorer.MsmqAgent -c Release -o C:\Services\DqeMsmqAgent
sc.exe create DqeMsmqAgent binPath= "C:\Services\DqeMsmqAgent\DynaQueueExplorer.MsmqAgent.exe" start= auto obj= "DOMAIN\svc-account" password= "…"
sc.exe start DqeMsmqAgent
```

It also runs from a console (`DynaQueueExplorer.MsmqAgent.exe`) for testing. `Msmq:ActiveRefreshInterval` (30 s),
`Msmq:IdleAfter` (5 min), `Msmq:RequestTimeout` (2 min), `Msmq:WarmUpOnStart` and `Msmq:BrowseTruncateBytes` tune the
explorer side.

### Migration bridges

```json
"MigrationBridges": [
  { "Name": "T1", "RabbitMqCluster": "ittest", "Vhosts": ["*.t1.x.bridge"], "MsmqConnections": ["msmq-ba-wxi001", "dyn-ba-wxi001"] }
]
```

Per environment: the RabbitMQ cluster, the bridge vhosts (glob, case-insensitive) and the MSMQ connections of the same
environment. Queue names are compared case-insensitively, without MSMQ's `private$\` prefix. Shovel flows need no
configuration. Users see the steps they may read (queues.read, exchanges.read; shovels only with shovels.read).

### RabbitMQ service account

The explorer works with a non-administrator account too: whatever the account may not read is reported in the
overview ("Limited visibility", with the missing tag) and the rest keeps working. Verified on RabbitMQ 4.1:

| capability | needs |
|---|---|
| queues, exchanges, bindings, depths, browse, export, failure analysis | `management` tag + **read** permission on the vhosts |
| consumer counts, stopped endpoints, connection totals | `monitoring` tag (with `management` only, RabbitMQ lists just the account's own consumers/connections) |
| node resources, shovel status | `monitoring` tag |
| shovel definitions | `policymaker` or `administrator` tag |
| move, copy, retry, publish from staging | **write** permission on the destination vhost |
| delete messages, purge | **read** permission — in RabbitMQ, read is enough to consume, delete and purge, so a "read-only" account can remove messages; the explorer's RBAC is what guards these actions |

Recommended for production: `monitoring` tag with `read` (and `write` where messages are moved/republished) on the
vhosts the explorer should cover; `policymaker` only if shovel definitions must be visible. When RabbitMQ refuses an
operation to the account, the API answers *502 RabbitMQ refused the service account* with RabbitMQ's reason, and
messages involved stay where they were.

### IBM MQ service account

| capability | needs |
|---|---|
| connect | `+connect +inq` on the queue manager |
| queue list, depths, open handles, queue manager info (PCF) | `+put` on `SYSTEM.ADMIN.COMMAND.QUEUE`, `+inq +dsp +get` on the model queue used for replies (`SYSTEM.DEFAULT.MODEL.QUEUE`), `+dsp` on the queues |
| last put/get, uncommitted messages | `+dsp` on the queues (queue status) and queue monitoring on |
| browse | `+browse` (and `+inq`) on the queue |
| without PCF rights | `+inq` on each queue listed in `Clusters[].Queues` |

Queues the account may not display are simply not listed, and the overview says so under "Limited visibility".
Errors always carry the MQ reason code, its constant and its meaning (e.g. `2035 MQRC_NOT_AUTHORIZED`). Calling an
operation that is not implemented for IBM MQ yet (move, copy, delete, purge, export, staging, trend, failure analysis)
answers *400 Not supported for IBM MQ*, and the UI does not offer it (the same holds for MSMQ).

### Test users (Development only, `appsettings.Development.json`)

| user      | password  | access |
|-----------|-----------|--------|
| `admin`   | `admin`   | everything (group `admins` → role *Administrators*) |
| `support` | `support` | read everything; browse, export, move, delete and republish in vhosts `sales` and `billing`; staging |
| `sales`   | `sales`   | vhost `sales` only: `Sales.*` queues and `error`, exchanges; browse `error` |
| `nobody`  | `nobody`  | no roles |

Roles are seeded on first start (`DevelopmentSeeder`); migrations run automatically in Development.

## Development

```sh
dotnet test DynaQueueExplorer.sln     # unit tests
cd src/web && npx ng build           # frontend build (production)
```

## Roadmap / known limitations

- Login: hardcoded users for the test phase → OIDC (groups map onto the same role assignments). No role management UI yet.
- Cluster credentials in configuration → secret store (Key Vault / Kubernetes secrets).
- Importing QueueExplorer's proprietary `.mq` files (needs sample files to reverse-engineer the format).
- Shovel management; SignalR push instead of polling; client certificates (mutual TLS) for RabbitMQ (IBM MQ has them).
- The overview trend lives in memory: it restarts empty with the API (persisting it is a small step).
- Browsing streams; AMQP 1.0 browse with the `modified` outcome to avoid incrementing the quorum delivery count.
- IBM MQ operations: move, copy, delete, export and staging (browse and decoding are done); channel status; topics.
- Kafka (the staging area already records the broker of each message).
- MSMQ operations: move, retry, delete, export and staging; journal queues (peek is done) — see [docs/msmq-support.md](docs/msmq-support.md).
