import { Binding, Exchange, Queue } from '../core/models';
import { isNsbInfrastructure } from './format';

/**
 * NServiceBus conventional routing topology on RabbitMQ:
 * - every message type has a fanout exchange named "Namespace:TypeName"; a type exchange is bound to the exchanges of
 *   its base types/interfaces, so publishing a derived event also reaches subscribers of its bases;
 * - every endpoint has a fanout exchange named after it, bound to its input queue;
 * - subscribing binds an event (type) exchange to the subscriber's endpoint exchange.
 */
export type ExchangeKind = 'event' | 'endpoint' | 'delay' | 'builtin' | 'other';

export interface Subscriber {
  endpoint: string;
  /** Base-type exchanges the event travels through before reaching the endpoint (empty: subscribed directly). */
  via: string[];
  queue?: Queue;
}

export interface Subscription {
  event: string;
  via: string[];
}

export function exchangeKind(e: Exchange, bindingsFrom: (vhost: string, source: string) => Binding[]): ExchangeKind {
  if (e.name === '' || e.name.startsWith('amq.')) return 'builtin';
  if (isNsbInfrastructure(e.name)) return 'delay';
  if (e.name.includes(':')) return 'event';
  return bindingsFrom(e.vhost, e.name).some((b) => b.destinationType === 'queue' && b.destination === e.name) ? 'endpoint' : 'other';
}

/** "Sales.Messages.Events:OrderPlaced" → { name: "OrderPlaced", ns: "Sales.Messages.Events" } */
export function eventName(exchange: string): { name: string; ns: string } {
  const i = exchange.lastIndexOf(':');
  return i < 0 ? { name: exchange, ns: '' } : { name: exchange.slice(i + 1), ns: exchange.slice(0, i) };
}

/** Endpoints that receive an event, directly or through the exchanges of its base types. */
export function subscribersOf(
  vhost: string,
  event: string,
  bindingsFrom: (vhost: string, source: string) => Binding[],
  queue: (vhost: string, name: string) => Queue | undefined,
): Subscriber[] {
  const result = new Map<string, Subscriber>();
  const visit = (exchange: string, via: string[], seen: Set<string>) => {
    for (const b of bindingsFrom(vhost, exchange)) {
      if (b.destinationType === 'queue') {
        // Not the conventional topology, but a queue bound to the event exchange receives it too.
        if (!result.has(b.destination)) result.set(b.destination, { endpoint: b.destination, via, queue: queue(vhost, b.destination) });
        continue;
      }
      if (seen.has(b.destination)) continue;
      seen.add(b.destination);
      if (b.destination.includes(':')) {
        visit(b.destination, [...via, b.destination], seen);
      } else if (!result.has(b.destination)) {
        result.set(b.destination, { endpoint: b.destination, via, queue: queue(vhost, b.destination) });
      }
    }
  };
  visit(event, [], new Set([event]));
  return [...result.values()].sort((a, b) => a.via.length - b.via.length || a.endpoint.localeCompare(b.endpoint));
}

/** Events an endpoint receives: the event exchanges bound to it, plus the derived events of those (base types). */
export function subscriptionsOf(
  vhost: string,
  endpoint: string,
  bindingsTo: (type: 'queue' | 'exchange', vhost: string, name: string) => Binding[],
): Subscription[] {
  const result = new Map<string, Subscription>();
  const visit = (exchange: string, via: string[], seen: Set<string>) => {
    for (const b of bindingsTo('exchange', vhost, exchange)) {
      if (!b.source.includes(':') || seen.has(b.source)) continue;
      seen.add(b.source);
      if (!result.has(b.source)) result.set(b.source, { event: b.source, via });
      visit(b.source, [b.source, ...via], seen);
    }
  };
  visit(endpoint, [], new Set());
  return [...result.values()].sort((a, b) => a.via.length - b.via.length || eventName(a.event).name.localeCompare(eventName(b.event).name));
}
