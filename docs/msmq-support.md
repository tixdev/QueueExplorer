# MSMQ support (design notes)

Status: **parked** — analysed, not scheduled. Pick this up before implementing anything for MSMQ.

## Goal

Browse (and later move, retry, export) messages of MSMQ queues on remote Windows machines from DynaQueueExplorer,
which runs cross-platform (Linux/macOS containers, no Windows APIs). Today the team does this with QueueExplorer on
Windows workstations, so remote reading of the queues is already allowed by the network and by queue permissions.

## Options

### A. Native protocols, implemented cross-platform (no installation on the MSMQ servers)

Microsoft publishes the MSMQ protocol specifications (Open Specifications, `[MS-MQ*]`). The relevant ones:

| spec | purpose | use for us |
|---|---|---|
| **[MS-MQRR]** Message Queuing Remote Read Protocol | open a remote queue, peek/receive, cursors | **reading remote queues** — what Windows itself uses when a client reads another machine's queue |
| [MS-MQMR] Queue Manager Management Protocol | queue manager / queue information | listing queues, depths |
| [MS-MQMQ] Data Structures | message packet and property formats | decoding messages |
| [MS-MQQB] Binary Reliable Message Transfer (TCP 1801) | queue manager to queue manager *sending* | publishing to a remote queue (move/retry) |
| [MS-MQDMPR], [MS-MQCN], [MS-MQDS] | common model, notifications, directory service (AD) | public queues / AD integration only |
| [MC-MQSRM] SOAP Reliable Messaging (SRMP over HTTP) | sending over HTTP | only if MSMQ HTTP support is installed; send only |

What has to be built:

1. **DCE/RPC client with NDR marshaling** ([MS-RPCE]): endpoint mapper (TCP 135) to resolve the dynamic port, then the
   MS-MQRR interface (the MSMQ RPC ports are typically 2101/2103/2105). No maintained managed library exists;
   impacket (Python) implements DCE/RPC + NTLM and is a useful reference.
2. **Windows authentication at RPC level**: NTLM/Kerberos with packet integrity/privacy. .NET's
   `NegotiateAuthentication` (cross-platform since .NET 7) can provide the tokens; wiring it into the RPC security
   trailer is the delicate part. Kerberos on Linux/macOS needs a keytab/krb5 configuration. The account needs
   *Peek/Receive* rights on the queues (same as for QueueExplorer users today).
3. **Message decoding**: properties per [MS-MQMQ]; bodies from .NET Framework apps are usually `XmlMessageFormatter`
   (XML) or `BinaryMessageFormatter` (BinaryFormatter — show as hex/text, **never deserialize**). NServiceBus on MSMQ
   stores its headers as XML in the message *Extension* property, so the existing NServiceBus decoder can be reused
   once those headers are extracted.
4. Cursor-based **peek** is side-effect free on MSMQ (unlike RabbitMQ's get/requeue): browsing does not change the
   queue, so no delivery-count concerns.

Risks: weeks of work; compatibility across Windows/MSMQ versions; authentication edge cases; a component that is hard
to maintain. Network: QueueExplorer proves the RPC ports are open **from the Windows workstations**; it must be checked
that the same ports (135, the dynamic MSMQ RPC ports, 1801 for sending) are reachable **from the network where
DynaQueueExplorer runs**.

### B. Windows agent (preferred technically)

A small Windows service on (or next to) the MSMQ servers, using the native MSMQ API and exposing an authenticated
HTTPS API (client certificate or token) that DynaQueueExplorer calls as one more broker type (`msmq`):

- native, fully compatible behaviour: true peek, subqueues (`MoveMessage`), journal and system dead-letter queues, purge;
- fits the existing architecture: snapshot-style bulk listing, the staging area (broker `msmq`), `.mqx` export;
- much less code and risk than option A.

**On hold**: installing a service on the MSMQ servers requires lengthy internal approval processes.

## Decision so far

- Option B is the shortest and safest path but is blocked by company processes for now.
- Option A is feasible because the network already allows remote reading, at a much higher cost and risk.
- Revisit when MSMQ support is prioritised. Open questions then: which queues/endpoints still run on MSMQ (e.g.
  NServiceBus MSMQ transport), whether the need is migration support or day-to-day operations, and the
  authentication setup (domain account, Kerberos availability from the DynaQueueExplorer host).

## Fit with the existing code

Whatever the option, MSMQ would plug in as another broker next to RabbitMQ: a client project
(`DynaQueueExplorer.Msmq`), a snapshot provider for queue listing/depths, a message reader producing the same
`BrowsedMessage` view model, and staging messages with `Broker = "msmq"` (the staging area already records the broker
of every batch and message, and publishing is only offered towards a broker of the same kind).
