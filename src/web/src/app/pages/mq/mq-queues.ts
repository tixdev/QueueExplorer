import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ClusterStore } from '../../core/cluster-store';
import { Queue } from '../../core/models';
import { DateTimePipe, NumberPipe, RelativePipe } from '../../shared/format';
import { matchesSearch, persistedSignal } from '../../shared/list-filters';
import { DepthMeter, QueueTypeLabels, fillRatio, isSystemQueue } from './mq';

type SortKey = 'name' | 'depth' | 'fill' | 'lastPut';

/** Queues of an IBM MQ queue manager, with MQ's own columns (depth vs max depth, open handles, inhibited flags). */
@Component({
  selector: 'app-mq-queues',
  imports: [ScrollingModule, RouterLink, NumberPipe, RelativePipe, DateTimePipe, DepthMeter],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './mq-queues.html',
})
export class MqQueuesPage {
  readonly store = inject(ClusterStore);
  readonly typeLabels = QueueTypeLabels;
  readonly types = ['local', 'alias', 'remote', 'xmitq', 'model'];

  constructor() {
    // Same query parameters as the RabbitMQ list, so links from the overview work for both.
    const query = inject(ActivatedRoute).snapshot.queryParamMap;
    if (query.has('withMessages')) this.onlyWithMessages.set(query.get('withMessages') === '1');
    const sort = query.get('sort');
    if (sort === 'messages') this.sort.set('depth');
    else if (sort === 'name' || sort === 'depth' || sort === 'fill' || sort === 'lastPut') this.sort.set(sort);
    if (query.has('type')) this.type.set(query.get('type') ?? '');
  }

  readonly search = persistedSignal('mq.queues.search', '');
  readonly type = persistedSignal('mq.queues.type', '');
  readonly hideSystem = persistedSignal('mq.queues.hideSystem', true);
  readonly onlyWithMessages = persistedSignal('mq.queues.onlyWithMessages', false);
  readonly sort = persistedSignal<SortKey>('mq.queues.sort', 'name');

  /** The relative times move with every snapshot refresh. */
  readonly now = computed(() => this.store.checkedAt() ?? Date.now());

  readonly queues = computed(() => {
    const all = this.store.topology()?.queues ?? [];
    const search = this.search().trim();
    const type = this.type();
    const hideSystem = this.hideSystem();
    const onlyWithMessages = this.onlyWithMessages();
    return all
      .filter(
        (q) =>
          (!type || q.type === type) &&
          (!hideSystem || !isSystemQueue(q.name)) &&
          (!onlyWithMessages || (q.messages ?? 0) > 0) &&
          matchesSearch(q.name + ' ' + (q.target ?? '') + ' ' + (q.description ?? ''), search),
      )
      .sort(comparer(this.sort()));
  });

  readonly totals = computed(() => {
    const list = this.queues();
    return { count: list.length, messages: list.reduce((sum, q) => sum + (q.messages ?? 0), 0) };
  });

  trackQueue(_: number, q: Queue): string {
    return q.name;
  }

  /** Nobody has the queue open for input while messages wait: worth a glance (transmission queues excluded). */
  unread(q: Queue): boolean {
    return (q.messages ?? 0) > 0 && q.consumers === 0 && q.type === 'local';
  }
}

function comparer(sort: SortKey): (a: Queue, b: Queue) => number {
  const byName = (a: Queue, b: Queue) => a.name.localeCompare(b.name);
  switch (sort) {
    case 'depth':
      return (a, b) => (b.messages ?? -1) - (a.messages ?? -1) || byName(a, b);
    case 'fill':
      return (a, b) => (fillRatio(b) ?? -1) - (fillRatio(a) ?? -1) || byName(a, b);
    case 'lastPut':
      return (a, b) => (b.lastPut ?? '').localeCompare(a.lastPut ?? '') || byName(a, b);
    default:
      return byName;
  }
}
