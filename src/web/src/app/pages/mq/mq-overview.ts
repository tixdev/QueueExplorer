import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { EMPTY } from 'rxjs';
import { Api } from '../../core/api';
import { ClusterStore } from '../../core/cluster-store';
import { Queue } from '../../core/models';
import { poll } from '../../core/polling';
import { NumberPipe } from '../../shared/format';
import { Status, StatusIcon } from '../../shared/status-icon';
import { fillRatio, isSystemQueue } from './mq';

interface HealthItem {
  status: Status;
  text: string;
  detail?: string;
  link?: unknown[];
}

/** Overview of an IBM MQ queue manager: dead-letter queue, queues filling up, queues nobody reads, transmission backlog. */
@Component({
  selector: 'app-mq-overview',
  imports: [RouterLink, NumberPipe, StatusIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './mq-overview.html',
})
export class MqOverviewPage {
  private readonly api = inject(Api);
  readonly store = inject(ClusterStore);

  // Served from the server's snapshot, like the queue list: polling does not reach the queue manager.
  private readonly overviewPoll = poll(() => {
    const c = this.store.cluster();
    return c ? this.api.overview(c) : EMPTY;
  }, 5000);

  readonly error = this.overviewPoll.error;
  readonly qmgr = computed(() => this.overviewPoll.value()?.queueManager);
  readonly totalDepth = computed(() => this.overviewPoll.value()?.overview?.queueTotals?.messages);
  /** Application queues: SYSTEM.* queues only appear where they matter (dead-letter, transmission). */
  readonly queues = computed(() => (this.store.topology()?.queues ?? []).filter((q) => !isSystemQueue(q.name) || q.type === 'xmitq'));

  readonly deadLetter = computed(() => {
    const name = this.qmgr()?.deadLetterQueue;
    return name ? this.store.queue(this.qmgr()!.name, name) : undefined;
  });
  readonly nearlyFull = computed(() =>
    this.queues()
      .filter((q) => (fillRatio(q) ?? 0) >= 0.8)
      .sort((a, b) => (fillRatio(b) ?? 0) - (fillRatio(a) ?? 0)),
  );
  /** Local queues with messages and no open input handle: whoever should read them is not running. */
  readonly unread = computed(() =>
    this.queues()
      .filter((q) => q.type === 'local' && (q.messages ?? 0) > 0 && q.consumers === 0 && q.name !== this.qmgr()?.deadLetterQueue)
      .sort(byDepth),
  );
  readonly transmissionBacklog = computed(() => this.queues().filter((q) => q.type === 'xmitq' && (q.messages ?? 0) > 0).sort(byDepth));
  readonly topQueues = computed(() => this.queues().filter((q) => (q.messages ?? 0) > 0).sort(byDepth).slice(0, 8));
  readonly topMax = computed(() => this.topQueues()[0]?.messages ?? 1);

  readonly limitations = computed(() => (this.store.topology()?.snapshot ?? this.overviewPoll.value()?.snapshot)?.limitations ?? []);

  readonly health = computed<HealthItem[]>(() => {
    const items: HealthItem[] = [];
    const c = this.store.cluster();
    const snapshot = this.store.topology()?.snapshot ?? this.overviewPoll.value()?.snapshot;
    if (snapshot?.lastError) items.push({ status: 'critical', text: 'Cannot refresh from the queue manager', detail: snapshot.lastError });

    for (const q of this.queues().filter((q) => (fillRatio(q) ?? 0) >= 1))
      items.push({ status: 'critical', text: `${q.name} is full`, detail: 'Puts fail with MQRC_Q_FULL (2053)', link: queueLink(c, q) });
    for (const q of this.queues().filter((q) => q.inhibitGet && (q.messages ?? 0) > 0))
      items.push({ status: 'serious', text: `${q.name}: get inhibited with ${q.messages} message${q.messages === 1 ? '' : 's'} waiting`, link: queueLink(c, q) });
    for (const q of this.queues().filter((q) => q.inhibitPut && (q.openOutput ?? 0) > 0))
      items.push({ status: 'warning', text: `${q.name}: put inhibited while ${q.openOutput} application handle${q.openOutput === 1 ? ' has' : 's have'} it open`, link: queueLink(c, q) });

    const backlog = this.transmissionBacklog();
    if (backlog.length)
      items.push({
        status: 'serious',
        text: `${backlog.length} transmission queue${backlog.length > 1 ? 's' : ''} with a backlog`,
        detail: 'The sender channel is probably not running: ' + backlog.slice(0, 3).map((q) => `${q.name} (${q.messages})`).join(', '),
        link: ['/c', c, 'queues'],
      });

    const dlq = this.deadLetter();
    if (dlq && (dlq.messages ?? 0) > 0)
      items.push({ status: 'warning', text: `${(dlq.messages ?? 0).toLocaleString('en-GB')} messages on the dead-letter queue`, detail: dlq.name, link: queueLink(c, dlq) });
    if (!this.qmgr()?.deadLetterQueue && this.qmgr())
      items.push({ status: 'warning', text: 'No dead-letter queue defined', detail: 'Undeliverable messages block channels instead of being set aside' });

    const near = this.nearlyFull().filter((q) => (fillRatio(q) ?? 0) < 1);
    if (near.length)
      items.push({ status: 'warning', text: `${near.length} queue${near.length > 1 ? 's' : ''} at 80% of max depth or more`, detail: near.slice(0, 3).map((q) => q.name).join(', ') });

    const unread = this.unread().length;
    if (unread) items.push({ status: 'warning', text: `${unread} queue${unread > 1 ? 's' : ''} with messages and nobody reading` });
    return items;
  });

  readonly worst = computed<Status>(() => {
    const order: Status[] = ['critical', 'serious', 'warning'];
    return order.find((s) => this.health().some((h) => h.status === s)) ?? 'good';
  });
}

function byDepth(a: Queue, b: Queue): number {
  return (b.messages ?? 0) - (a.messages ?? 0) || a.name.localeCompare(b.name);
}

function queueLink(cluster: string | null, q: Queue): unknown[] {
  return ['/c', cluster, 'queues', q.vhost, q.name];
}
