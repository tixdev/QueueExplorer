import { Queue } from '../../core/models';

/**
 * MSMQ queue helpers. In the shared Queue shape: vhost = machine, type = kind (private, outgoing, deadletter),
 * messages = messages in the queue, state = connection state of an outgoing queue.
 */

export const KindLabels: Record<string, string> = {
  private: 'private',
  outgoing: 'outgoing',
  deadletter: 'dead-letter',
};

const DEAD_LETTER_NAMES: Record<string, string> = {
  'system$;deadxact': 'Transactional dead-letter queue',
  'system$;deadletter': 'Dead-letter queue',
};

/** "private$\orders" → "orders"; dead-letter queues by their role; outgoing queues keep their destination. */
export function shortName(q: Queue): string {
  if (q.type === 'deadletter') return DEAD_LETTER_NAMES[q.name.toLowerCase()] ?? q.name;
  return q.name.replace(/^private\$\\/i, '');
}

export function isOutgoing(q: Queue): boolean {
  return q.type === 'outgoing';
}

export function isDeadLetter(q: Queue): boolean {
  return q.type === 'deadletter';
}

/** NServiceBus error queues: "error" or a name ending in ".error". */
export function isErrorQueue(q: Queue): boolean {
  const name = shortName(q).toLowerCase();
  return q.type === 'private' && (name === 'error' || name.endsWith('.error'));
}

const INFRASTRUCTURE = ['.retries', '.timeouts', '.timeoutsdispatcher', '.distributor.control', '.distributor.storage', '.subscriptions'];

/**
 * Queues NServiceBus creates next to each endpoint for its own use. The distributor's storage queue always holds one
 * message per ready worker, so it is not a backlog.
 */
export function isNsbInfrastructure(q: Queue): boolean {
  const name = q.name.toLowerCase();
  return q.type === 'private' && INFRASTRUCTURE.some((suffix) => name.endsWith(suffix));
}

/** Messages waiting on an outgoing queue whose connection is not up: the destination machine is not reachable. */
export function isStuckOutgoing(q: Queue): boolean {
  return isOutgoing(q) && (q.messages ?? 0) > 0 && q.state !== 'connected' && q.state !== 'local connection';
}

export function messagesLabel(q: Queue): string {
  const n = q.messages;
  if (n === undefined || n === null) return '–';
  return (q.atLeast ? '≥ ' : '') + n.toLocaleString('en-GB');
}
