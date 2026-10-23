# Export, import and staging area (design)

Status: implemented (export, import, staging, editing, publish, broker type per batch/message). Not yet implemented:
importing QueueExplorer `.mq` files, resumable publishing of very large batches, IBM MQ/Kafka messages. See the README for the user-facing
description; this document records the design decisions.

## Goals

- Save messages from a queue to a file and import them back, like QueueExplorer's `.mq` files.
- Import a file into a **staging area** first, inspect (and optionally edit or discard) the messages,
  then decide where to send them: any queue or exchange the user can publish to, in any vhost or cluster.

## File format: `.mqx`

A zip archive, so it stays inspectable with standard tools and handles binary bodies without base64 bloat:

```
manifest.json            format version, created at/by, source (cluster, vhost, queue), message count
messages/000001.json     exchange, routing key, AMQP properties, headers (typed), body file name, original index
messages/000001.body     raw body bytes, byte-for-byte
...
```

Header values keep their AMQP type (string, long, bool, table, array, …) so a round-trip is lossless,
`x-death` included. Importing QueueExplorer `.mq` files needs sample files to reverse-engineer the format.

## Staging area

Persisted in SQL Server, so it is shared within the team and survives restarts:

- `StagingBatch`: id, name, created by/at, origin (file name or cluster/vhost/queue), status.
- `StagedMessage`: batch id, position, properties/headers (JSON), body (`varbinary(max)`),
  edited flag, discarded flag, last publish result.

Sources: file import, or "copy/move to staging" from a queue (the selected messages or the first N).
When moving, the batch is committed to SQL Server before the messages are acknowledged on the queue.
The staging view reuses the message viewer (NServiceBus decoding included) and supports bulk selection.
Each batch and message records its broker (`rabbitmq`, later `ibmmq`, `kafka`): the list can be filtered by it and
publishing is only offered towards a broker of the same kind. Editing (RabbitMQ) covers body, basic properties and
headers; untouched headers keep their AMQP type, the original is saved on the first edit and can be restored.

## Publishing

- Permissions: `staging.read`, `staging.write` (import, discard, delete), `messages.publish`
  (queue/exchange scope, checked on the **destination**), `messages.export` (queue scope).
- Publishing uses **one AMQP connection per cluster** (long-lived, shared), a channel per operation,
  **publisher confirms** and batches, instead of one management-API HTTP call per message.
- Each message records when and where it was last published; failures (unroutable, nacked) are reported per message.
  NServiceBus failure headers can be dropped when re-sending a failed message to its `FailedQ`, as ServiceControl
  does on retry; broker-managed headers (`x-delivery-count`, `x-death`, …) are always dropped.
- Every import, export, edit and publish is written to the audit log with counts and destination.
