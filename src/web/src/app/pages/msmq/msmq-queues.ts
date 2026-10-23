import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ClusterStore } from '../../core/cluster-store';
import { Queue } from '../../core/models';
import { BytesPipe, NumberPipe } from '../../shared/format';
import { matchesSearch, persistedSignal } from '../../shared/list-filters';
import { KindLabels, isErrorQueue, isNsbInfrastructure, isStuckOutgoing, messagesLabel, shortName } from './msmq';

type SortKey = 'name' | 'messages' | 'bytes';

/** Queues of an MSMQ machine: private queues, the dead-letter queues and the outgoing queues towards other machines. */
@Component({
  selector: 'app-msmq-queues',
  imports: [ScrollingModule, RouterLink, NumberPipe, BytesPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './msmq-queues.html',
})
export class MsmqQueuesPage {
  readonly store = inject(ClusterStore);
  readonly kindLabels = KindLabels;
  readonly kinds = ['private', 'deadletter', 'outgoing'];
  readonly shortName = shortName;
  readonly messagesLabel = messagesLabel;
  readonly isErrorQueue = isErrorQueue;
  readonly isStuck = isStuckOutgoing;

  constructor() {
    // Same query parameters as the other brokers' lists, so links from the overview work for all of them.
    const query = inject(ActivatedRoute).snapshot.queryParamMap;
    if (query.has('withMessages')) this.onlyWithMessages.set(query.get('withMessages') === '1');
    const sort = query.get('sort');
    if (sort === 'messages' || sort === 'depth') this.sort.set('messages');
    else if (sort === 'name' || sort === 'bytes') this.sort.set(sort);
    if (query.has('type')) this.kind.set(query.get('type') ?? '');
  }

  readonly search = persistedSignal('msmq.queues.search', '');
  readonly kind = persistedSignal('msmq.queues.kind', '');
  readonly hideInfrastructure = persistedSignal('msmq.queues.hideInfra', true);
  readonly onlyWithMessages = persistedSignal('msmq.queues.onlyWithMessages', false);
  readonly sort = persistedSignal<SortKey>('msmq.queues.sort', 'name');

  readonly queues = computed(() => {
    const all = this.store.topology()?.queues ?? [];
    const search = this.search().trim();
    const kind = this.kind();
    const hideInfra = this.hideInfrastructure();
    const onlyWithMessages = this.onlyWithMessages();
    return all
      .filter(
        (q) =>
          (!kind || q.type === kind) &&
          (!hideInfra || !isNsbInfrastructure(q)) &&
          (!onlyWithMessages || (q.messages ?? 0) > 0) &&
          matchesSearch(q.name, search),
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
}

function comparer(sort: SortKey): (a: Queue, b: Queue) => number {
  const byName = (a: Queue, b: Queue) => a.name.localeCompare(b.name);
  switch (sort) {
    case 'messages':
      return (a, b) => (b.messages ?? -1) - (a.messages ?? -1) || byName(a, b);
    case 'bytes':
      return (a, b) => (b.bytes ?? -1) - (a.bytes ?? -1) || byName(a, b);
    default:
      return byName;
  }
}
