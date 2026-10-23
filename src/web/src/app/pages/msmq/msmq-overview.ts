import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { EMPTY } from 'rxjs';
import { Api } from '../../core/api';
import { Auth } from '../../core/auth';
import { ClusterStore } from '../../core/cluster-store';
import { Actions, Queue } from '../../core/models';
import { poll } from '../../core/polling';
import { NumberPipe, RelativePipe } from '../../shared/format';
import { Status, StatusIcon } from '../../shared/status-icon';
import { isDeadLetter, isErrorQueue, isNsbInfrastructure, isOutgoing, isStuckOutgoing, messagesLabel, shortName } from './msmq';

interface HealthItem {
  status: Status;
  text: string;
  detail?: string;
  link?: unknown[];
  query?: Record<string, string | number>;
}

/** Overview of an MSMQ machine: dead-letter queues, NServiceBus error queues, outgoing backlog, queues with most messages. */
@Component({
  selector: 'app-msmq-overview',
  imports: [RouterLink, NumberPipe, RelativePipe, StatusIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './msmq-overview.html',
})
export class MsmqOverviewPage {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  readonly store = inject(ClusterStore);
  readonly shortName = shortName;
  readonly messagesLabel = messagesLabel;

  // Served from the server's snapshot: polling does not reach the agent or the machine.
  private readonly overviewPoll = poll(() => {
    const c = this.store.cluster();
    return c ? this.api.overview(c) : EMPTY;
  }, 10000);

  readonly error = this.overviewPoll.error;
  readonly machine = computed(() => this.overviewPoll.value()?.msmq);
  /** Placeholders until the first answers arrive: the first read of a machine through the agent takes seconds. */
  readonly loading = computed(() => {
    if (this.error() || this.store.error()) return false;
    const cluster = this.store.cluster() ?? '';
    const queuesPending = this.auth.can(cluster, Actions.queuesRead) && this.store.fullTopology() === undefined;
    return this.overviewPoll.value() === undefined || queuesPending;
  });

  /** Queues of the selected queue group (all without one). */
  readonly queues = computed(() => this.store.topology()?.queues ?? []);
  readonly local = computed(() => this.queues().filter((q) => !isOutgoing(q)));

  readonly totalMessages = computed(() => this.local().reduce((n, q) => n + (q.messages ?? 0), 0));
  readonly privateCount = computed(() => this.local().filter((q) => q.type === 'private').length);
  readonly deadLetters = computed(() => this.local().filter(isDeadLetter));
  readonly deadLetterMessages = computed(() => this.deadLetters().reduce((n, q) => n + (q.messages ?? 0), 0));
  readonly errorQueues = computed(() => this.local().filter((q) => isErrorQueue(q) && (q.messages ?? 0) > 0).sort(byMessages));
  readonly errorMessages = computed(() => this.errorQueues().reduce((n, q) => n + (q.messages ?? 0), 0));
  readonly outgoingBacklog = computed(() => this.queues().filter((q) => isOutgoing(q) && (q.messages ?? 0) > 0).sort(byMessages));
  readonly outgoingMessages = computed(() => this.outgoingBacklog().reduce((n, q) => n + (q.messages ?? 0), 0));
  readonly stuckOutgoing = computed(() => this.outgoingBacklog().filter(isStuckOutgoing));
  readonly topQueues = computed(() =>
    this.local()
      .filter((q) => q.type === 'private' && (q.messages ?? 0) > 0 && !isNsbInfrastructure(q))
      .sort(byMessages)
      .slice(0, 8),
  );
  readonly topMax = computed(() => this.topQueues()[0]?.messages ?? 1);

  readonly limitations = computed(() => (this.store.topology()?.snapshot ?? this.overviewPoll.value()?.snapshot)?.limitations ?? []);

  readonly health = computed<HealthItem[]>(() => {
    const items: HealthItem[] = [];
    const c = this.store.cluster();
    const snapshot = this.store.topology()?.snapshot ?? this.overviewPoll.value()?.snapshot;
    if (snapshot?.lastError) items.push({ status: 'critical', text: 'Cannot read the machine through the MSMQ agent', detail: snapshot.lastError });

    const stuck = this.stuckOutgoing();
    if (stuck.length)
      items.push({
        status: 'serious',
        text: `${stuck.length} outgoing queue${stuck.length > 1 ? 's' : ''} with messages not delivered`,
        detail: stuck.slice(0, 3).map((q) => `${q.name} (${q.messages}, ${q.state ?? 'state unknown'})`).join(', '),
        link: ['/c', c, 'queues'],
        query: { type: 'outgoing', withMessages: 1, sort: 'messages' },
      });
    for (const q of this.deadLetters().filter((q) => (q.messages ?? 0) > 0))
      items.push({ status: 'warning', text: `${messagesLabel(q)} messages in the ${shortName(q).toLowerCase()}`, link: queueLink(c, q) });

    const errors = this.errorMessages();
    if (errors)
      items.push({
        status: 'warning',
        text: `${errors.toLocaleString('en-GB')} messages in ${this.errorQueues().length} error queue${this.errorQueues().length > 1 ? 's' : ''}`,
        link: ['/c', c, 'queues'],
        query: { withMessages: 1, sort: 'messages' },
      });
    return items;
  });

  readonly worst = computed<Status>(() => {
    const order: Status[] = ['critical', 'serious', 'warning'];
    return order.find((s) => this.health().some((h) => h.status === s)) ?? 'good';
  });

  readonly checkedAt = computed(() => (this.store.topology()?.snapshot ?? this.overviewPoll.value()?.snapshot)?.fetchedAt);
}

function byMessages(a: Queue, b: Queue): number {
  return (b.messages ?? 0) - (a.messages ?? 0) || a.name.localeCompare(b.name);
}

function queueLink(cluster: string | null, q: Queue): unknown[] {
  return ['/c', cluster, 'queues', q.vhost, q.name];
}
