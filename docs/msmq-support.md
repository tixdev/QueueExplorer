# MSMQ support

Status: **implemented for reading** (overview, queues, peek with NServiceBus decoding) with option B below, the
Windows agent. Operations (move, retry, delete, export, staging) are not implemented yet.

## Goal

Browse (and later move, retry, export) messages of MSMQ queues on remote Windows machines from DynaQueueExplorer,
which runs cross-platform (Linux/macOS containers, no Windows APIs). Today the team does this with QueueExplorer on
Windows workstations, so remote reading of the queues is already allowed by the network and by queue permissions.

## Options considered

### A. Native protocols, implemented cross-platform (no installation anywhere)

Microsoft publishes the MSMQ protocol specifications ([MS-MQRR] remote read, [MS-MQMR] management, [MS-MQMQ] data
structures, [MS-MQQB] sending). It would need a DCE/RPC client with NDR marshaling, NTLM/Kerberos at RPC level and the
message decoding: weeks of work and a component that is hard to maintain. **Not pursued.**

### B. Windows agent — **chosen**

A small Windows service using the native MSMQ API and answering the explorer over HTTP(S) with a shared key. The
explorer calls it as one more broker type (`msmq`).

The original concern was that installing a service *on the MSMQ servers* needs lengthy approvals. Measurements showed
it is not necessary: like QueueExplorer, the agent reads the machines **remotely** from any Windows host with MSMQ
installed (a workstation or a utility server). Installing it on the servers later only makes it faster.

## How the agent reads a machine

| what | how | cost (remote, ittest) |
|---|---|---|
| private queues | `MSMQApplication.PrivateQueues` (COM) | 1 RPC — 1312 queues in 0.7 s |
| active queues (local and outgoing) | `MSMQApplication.ActiveQueues` | 1 RPC — ~0.2 s |
| depths, sizes, journal counts | `MSMQ Queue` performance counters, read once for all queues | ~1–10 s for 500 instances |
| depths when counters are not readable remotely | `MSMQManagement` per **active** queue (inactive queues are empty), 16 in parallel | ~25 ms per queue — 25–50 s for ~1000 active queues |
| outgoing queue state and next hops | `MQMgmtGetInfo` (mqrt.dll), only for outgoing queues with messages when counters exist | 1 RPC per queue |
| dead-letter queues (`system$;deadxact`, `system$;deadletter`) | counted by peeking headers, up to a limit | 0.03–0.3 s |
| peek | `MSMQQueue.PeekCurrent/PeekNext` with a cursor | ~90 ms per message |

On ittest the worker machines (`dyn-ba-wxi00x`) expose their counters remotely; the distributor machines
(`msmq-ba-wxi00x`) answer "network path not found" (Remote Registry not running) and use the per-queue fallback. The
explorer answers from its snapshot at once and refreshes it in the background, so the slower machines only delay the
"last read" time, not the pages.

Peeking is side-effect free on MSMQ (unlike RabbitMQ's get/requeue): no delivery counts, no reordering.

## Message decoding

- Native properties: id and correlation id in the System.Messaging form `{guid}\{sequence}`, label, **class** (for a
  dead-lettered message the reason, e.g. `0xC001 NACK: purged from the destination queue`), times, response / admin /
  destination queues, transaction, time-to-reach-queue / time-to-be-received, sender (SID translated to the account).
- NServiceBus on MSMQ stores its headers as XML in the message **extension** (`ArrayOfHeaderInfo`); they are parsed with
  DTDs prohibited and fed to the same NServiceBus decoder as RabbitMQ headers.
- Bodies are returned as bytes (COM string bodies as UTF-8) and formatted like the others (XML/JSON pretty-printed,
  binary as hex). Bodies serialized with `BinaryFormatter` are shown as bytes, **never deserialized**.

## Security

- The agent requires `X-DQE-Agent-Key` on every call (constant-time comparison) and only reads the machines listed in
  `Agent:Machines` (by default only its own), so it cannot be used to reach arbitrary hosts.
- It only accepts `private$\name` and the two system dead-letter queues as queue names, never a full format name.
- It listens on `localhost` by default; across hosts use HTTPS (Kestrel certificate).
- Users never talk to the agent: the explorer applies RBAC first (the machine is the *vhost* of grants) and audits browses.

## Queue groups

MSMQ environments are identified by machine (T1: `msmq-ba-wxi001` + `dyn-ba-wxi001`, T2: `…wxi002`, T3:
`msmq-ba-wxi003` + `dyn-ba-wxi005`), countries by queue name (CH: `*.ch.*`; BS: `*.bs.*` or a name starting with `bs`),
so the group rules use the `Cluster` (connection) pattern together with the `Queue` pattern.

## Next steps

- Operations: move/retry within and across machines (`MQSendMessage` in a transaction, remove from the source only
  after the send committed), delete, export to `.mqx`, staging with `Broker = "msmq"`.
- Journal queues (`private$\name;journal`) and subqueues (`;retry`, `;poison`).
- Agent on the MSMQ servers (local counters, sub-second reads) once approved.
