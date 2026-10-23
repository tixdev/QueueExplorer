import { Injectable, computed, inject } from '@angular/core';
import { persistedSignal } from '../shared/list-filters';
import { Auth } from './auth';
import { BrokerId, QueueGroup } from './models';

/** Membership test of one queue group on one connection, compiled once. */
export interface GroupMatcher {
  group: QueueGroup;
  /** False when no rule of the group concerns this broker/connection: the group selects nothing here. */
  applies: boolean;
  /** A queue (or, for RabbitMQ, an exchange of the same name) in the group. */
  contains(vhost: string, name: string): boolean;
  /** A vhost selected as a whole by a rule without queue pattern. */
  containsVhost(vhost: string): boolean;
}

/**
 * The queue group the user works in. It is chosen once and kept across connections and visits, so switching from a
 * RabbitMQ cluster to an IBM MQ queue manager stays within the same environment (e.g. "T1 CH").
 */
@Injectable({ providedIn: 'root' })
export class QueueGroups {
  private readonly auth = inject(Auth);
  private readonly selectedName = persistedSignal('queueGroup', '');

  readonly groups = computed(() => this.auth.me()?.queueGroups ?? []);
  /** Undefined for "all queues", or when the stored group no longer exists in the configuration. */
  readonly active = computed(() => this.groups().find((g) => g.name === this.selectedName()));

  select(name: string): void {
    this.selectedName.set(name);
  }

  matcher(group: QueueGroup, broker: BrokerId, cluster: string): GroupMatcher {
    const rules = group.rules
      .filter((r) => !r.broker || r.broker.toLowerCase() === broker)
      .filter((r) => !r.cluster || globRegExp(r.cluster).test(cluster))
      .map((r) => ({ vhost: r.vhost ? globRegExp(r.vhost) : null, queue: r.queue ? globRegExp(r.queue) : null }));
    return {
      group,
      applies: rules.length > 0,
      contains: (vhost, name) => rules.some((r) => (!r.vhost || r.vhost.test(vhost)) && (!r.queue || r.queue.test(name))),
      containsVhost: (vhost) => rules.some((r) => !r.queue && (!r.vhost || r.vhost.test(vhost))),
    };
  }
}

/** Same semantics as the server's Glob, case-insensitive. */
function globRegExp(pattern: string): RegExp {
  const source = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
  return new RegExp(`^${source}$`, 'is');
}
