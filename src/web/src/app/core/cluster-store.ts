import { Injectable, computed, inject, signal } from '@angular/core';
import { EMPTY, map } from 'rxjs';
import { Api } from './api';
import { Binding, Exchange, Queue, Topology } from './models';
import { poll } from './polling';

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

  readonly cluster = signal<string | null>(null);

  // Each result carries the cluster it was loaded for, so switching cluster never shows stale data.
  private readonly topologyPoll = poll(() => {
    const cluster = this.cluster();
    return cluster
      ? this.api.topology(cluster).pipe(map((t) => ({ etag: t.etag, body: { cluster, topology: t.body } })))
      : EMPTY;
  }, 5000);

  readonly topology = computed<Topology | undefined>(() => {
    const value = this.topologyPoll.value();
    return value?.cluster === this.cluster() ? value.topology : undefined;
  });
  readonly error = this.topologyPoll.error;
  readonly checkedAt = this.topologyPoll.checkedAt;
  readonly loading = computed(() => this.topology() === undefined && this.topologyPoll.error() === null);

  readonly queuesByKey = computed(() => new Map((this.topology()?.queues ?? []).map((q) => [queueKey(q.vhost, q.name), q])));

  readonly exchangesByKey = computed(
    () => new Map((this.topology()?.exchanges ?? []).map((e) => [queueKey(e.vhost, e.name), e])),
  );

  /** Bindings whose source is the given exchange. */
  readonly bindingsBySource = computed(() => groupBy(this.topology()?.bindings ?? [], (b) => queueKey(b.vhost, b.source)));

  /** Bindings towards the given queue or exchange (key includes the destination type). */
  readonly bindingsByDestination = computed(() =>
    groupBy(this.topology()?.bindings ?? [], (b) => b.destinationType + SEP + queueKey(b.vhost, b.destination)),
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
