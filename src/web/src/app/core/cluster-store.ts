import { Injectable, computed, inject, signal } from '@angular/core';
import { EMPTY, map } from 'rxjs';
import { Api } from './api';
import { Auth } from './auth';
import { Actions, Binding, BrokerId, Exchange, Queue, Topology } from './models';
import { poll } from './polling';
import { GroupMatcher, QueueGroups } from './queue-groups';

const SEP = '\u0000';

export function queueKey(vhost: string, name: string): string {
  return vhost + SEP + name;
}

/**
 * Topology of the selected cluster, shared by every page of the shell. One request feeds the queue,
 * exchange and binding views; the binding indexes are rebuilt only when the snapshot actually changes.
 */
@Injectable()
export class ClusterStore {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly queueGroups = inject(QueueGroups);

  readonly cluster = signal<string | null>(null);
  readonly broker = computed<BrokerId>(() => this.auth.me()?.clusters.find((c) => c.name === this.cluster())?.broker ?? 'rabbitmq');
  readonly isIbmMq = computed(() => this.broker() === 'ibmmq');
  /** Exchanges and shovels exist only on RabbitMQ. */
  readonly isRabbitMq = computed(() => this.broker() === 'rabbitmq');

  // Each result carries the cluster it was loaded for, so switching cluster never shows stale data.
  private readonly topologyPoll = poll(() => {
    const cluster = this.cluster();
    // Users without queue or exchange access (e.g. overview only) have no topology to load.
    const allowed = cluster && (this.auth.can(cluster, Actions.queuesRead) || this.auth.can(cluster, Actions.exchangesRead));
    return allowed
      ? this.api.topology(cluster).pipe(map((t) => ({ etag: t.etag, body: { cluster, topology: t.body } })))
      : EMPTY;
  }, 5000);

  /** The whole topology of the connection, whatever queue group is selected. */
  readonly fullTopology = computed<Topology | undefined>(() => {
    const value = this.topologyPoll.value();
    return value?.cluster === this.cluster() ? value.topology : undefined;
  });

  /** The selected queue group on this connection, or null for all queues. */
  readonly group = computed<GroupMatcher | null>(() => {
    const group = this.queueGroups.active();
    const cluster = this.cluster();
    return group && cluster ? this.queueGroups.matcher(group, this.broker(), cluster) : null;
  });

  /**
   * What the lists show: the topology narrowed to the selected queue group. Every page reads this one, so the group
   * applies everywhere at once; look-ups by name (queue(), exchange(), bindings) still see the whole connection, so
   * a detail page opened from a link outside the group keeps working.
   */
  readonly topology = computed<Topology | undefined>(() => {
    const full = this.fullTopology();
    const g = this.group();
    if (!full || !g) return full;
    const queues = full.queues.filter((q) => g.contains(q.vhost, q.name));
    const exchanges = full.exchanges.filter((e) => g.contains(e.vhost, e.name));
    const vhosts = new Set([...full.vhosts.filter((v) => g.containsVhost(v)), ...queues.map((q) => q.vhost), ...exchanges.map((e) => e.vhost)]);
    return {
      ...full,
      vhosts: full.vhosts.filter((v) => vhosts.has(v)),
      queues,
      exchanges,
      bindings: full.bindings.filter((b) => vhosts.has(b.vhost)),
    };
  });

  /** How much of the connection the selected group covers, for the group indicator. */
  readonly groupCoverage = computed(() => {
    const g = this.group();
    const full = this.fullTopology();
    if (!g || !full) return null;
    const queues = this.topology()?.queues ?? [];
    return {
      name: g.group.name,
      applies: g.applies,
      queues: queues.length,
      total: full.queues.length,
      messages: queues.reduce((n, q) => n + (q.messages ?? 0), 0),
    };
  });

  /** Whether a queue is in the selected group (always true without one); for server lists such as error queues. */
  inGroup(vhost: string, name: string): boolean {
    return this.group()?.contains(vhost, name) ?? true;
  }

  readonly error = this.topologyPoll.error;
  readonly checkedAt = this.topologyPoll.checkedAt;
  readonly loading = computed(() => this.fullTopology() === undefined && this.topologyPoll.error() === null);

  readonly queuesByKey = computed(() => new Map((this.fullTopology()?.queues ?? []).map((q) => [queueKey(q.vhost, q.name), q])));

  readonly exchangesByKey = computed(
    () => new Map((this.fullTopology()?.exchanges ?? []).map((e) => [queueKey(e.vhost, e.name), e])),
  );

  /** Bindings whose source is the given exchange. */
  readonly bindingsBySource = computed(() => groupBy(this.fullTopology()?.bindings ?? [], (b) => queueKey(b.vhost, b.source)));

  /** Bindings towards the given queue or exchange (key includes the destination type). */
  readonly bindingsByDestination = computed(() =>
    groupBy(this.fullTopology()?.bindings ?? [], (b) => b.destinationType + SEP + queueKey(b.vhost, b.destination)),
  );

  select(cluster: string): void {
    if (this.cluster() === cluster) return;
    this.cluster.set(cluster);
    this.topologyPoll.reload();
  }

  refresh(): void {
    const cluster = this.cluster();
    if (!cluster) return;
    this.api.refresh(cluster).subscribe(() => this.topologyPoll.reload());
  }

  queue(vhost: string, name: string): Queue | undefined {
    return this.queuesByKey().get(queueKey(vhost, name));
  }

  exchange(vhost: string, name: string): Exchange | undefined {
    return this.exchangesByKey().get(queueKey(vhost, name));
  }

  bindingsTo(type: 'queue' | 'exchange', vhost: string, name: string): Binding[] {
    return this.bindingsByDestination().get(type + SEP + queueKey(vhost, name)) ?? [];
  }

  bindingsFrom(vhost: string, exchange: string): Binding[] {
    return this.bindingsBySource().get(queueKey(vhost, exchange)) ?? [];
  }
}

function groupBy<T>(items: T[], key: (item: T) => string): Map<string, T[]> {
  const map = new Map<string, T[]>();
  for (const item of items) {
    const k = key(item);
    const list = map.get(k);
    if (list) list.push(item);
    else map.set(k, [item]);
  }
  return map;
}
