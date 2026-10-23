# DynaQueueExplorer

A web-based explorer for **RabbitMQ**, built for teams running **NServiceBus**. It replaces desktop tools such as
QueueExplorer (Windows-only, slow on large brokers) with a shared, fast web application that:

- shows the state of one or more RabbitMQ clusters — nodes, queues, exchanges, bindings, shovels — without
  requiring users to be RabbitMQ administrators;
- recognises NServiceBus messages and displays them in a structured way (message types, endpoints, failure details);
- moves, copies, retries, exports and imports messages safely;
- parks messages in a **staging area**, shared across brokers, to inspect and edit them before deciding where to
  send them;
- applies its own **role-based access control** on top of a single privileged service account, and records every
  sensitive operation in an **audit log**.

IBM MQ and Kafka support are planned; the code is organised so that other brokers can be added next to RabbitMQ.

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

## Architecture

```
src/server/
  DynaQueueExplorer.Core            RBAC model, cluster options, audit contract, message keys, body formatting
  DynaQueueExplorer.RabbitMq        management API client, snapshots, AMQP connections, move/copy/publish, .mqx format
  DynaQueueExplorer.NServiceBus     NServiceBus message recognition and decoding
  DynaQueueExplorer.Infrastructure  EF Core / SQL Server: roles, grants, assignments, audit, staging
  DynaQueueExplorer.Api             ASP.NET Core minimal API (.NET 10); serves the SPA
src/web/                           Angular 22 (zoneless, signals, CDK virtual scroll); UI in English
tests/DynaQueueExplorer.Tests       xUnit, including fixtures captured from real NServiceBus 10 failures
tools/perf-env/                    generators for an NServiceBus-like RabbitMQ topology and sample messages
tools/NsbSimulator/                real NServiceBus 10 endpoints that fail in different ways
docker-compose.yml                 RabbitMQ 4.1 (management + shovel, TLS) and SQL Server 2022
```

NuGet versions are managed centrally in `Directory.Packages.props` (CPM, transitive pinning); common build settings
live in `Directory.Build.props` (warnings are errors).

## Getting started

```sh
tools/perf-env/generate_certs.sh                    # local CA + server certificate for RabbitMQ TLS (once)
python3 -I tools/perf-env/generate_definitions.py   # 10 vhosts × ~210 queues/exchanges, ~3.5k bindings, shovels, policies
docker compose up -d
python3 -I tools/perf-env/seed_messages.py          # ~2.4k NServiceBus-shaped messages (error, audit, endpoints)

cd src/web && npm ci && npx ng build && cd -        # builds the SPA into the API's wwwroot
dotnet run --project src/server/DynaQueueExplorer.Api --launch-profile http
# → http://localhost:5044
```

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
| `LocalUsers[]` | test-phase users (`Username`, `Password`, `DisplayName`, `Groups`) |

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
- Shovel management; SignalR push instead of polling; client certificates (mutual TLS).
- The overview trend lives in memory: it restarts empty with the API (persisting it is a small step).
- Browsing streams; AMQP 1.0 browse with the `modified` outcome to avoid incrementing the quorum delivery count.
- IBM MQ and Kafka (the staging area already records the broker of each message).
