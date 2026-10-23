import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ClusterStore } from '../../core/cluster-store';
import { Queue } from '../../core/models';
import { NumberPipe, isNsbInfrastructure } from '../../shared/format';
import { matchesSearch, persistedSignal } from '../../shared/list-filters';

type SortKey = 'name' | 'messages' | 'consumers';

@Component({
  selector: 'app-queues',
  imports: [ScrollingModule, RouterLink, NumberPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './queues.html',
})
export class QueuesPage {
  readonly store = inject(ClusterStore);

  readonly vhost = persistedSignal('queues.vhost', '');
  readonly search = persistedSignal('queues.search', '');
  readonly hideInfrastructure = persistedSignal('queues.hideInfra', true);
  readonly onlyWithMessages = persistedSignal('queues.onlyWithMessages', false);
  readonly sort = persistedSignal<SortKey>('queues.sort', 'name');

  readonly vhosts = computed(() => this.store.topology()?.vhosts ?? []);

  readonly queues = computed(() => {
    const all = this.store.topology()?.queues ?? [];
    const vhost = this.vhost();
    const search = this.search().trim();
    const hideInfra = this.hideInfrastructure();
    const onlyWithMessages = this.onlyWithMessages();
    const filtered = all.filter(
      (q) =>
        (!vhost || q.vhost === vhost) &&
        (!hideInfra || !isNsbInfrastructure(q.name)) &&
        (!onlyWithMessages || (q.messages ?? 0) > 0) &&
        matchesSearch(q.name, search),
    );
    return filtered.sort(comparer(this.sort()));
  });

  readonly totals = computed(() => {
    const list = this.queues();
    return { count: list.length, messages: list.reduce((sum, q) => sum + (q.messages ?? 0), 0) };
  });

  trackQueue(_: number, q: Queue): string {
    return q.vhost + '/' + q.name;
  }

  isErrorQueue(q: Queue): boolean {
    return q.name === 'error' || q.name.endsWith('.error');
  }
}

function comparer(sort: SortKey): (a: Queue, b: Queue) => number {
  switch (sort) {
    case 'messages':
      return (a, b) => (b.messages ?? 0) - (a.messages ?? 0) || a.name.localeCompare(b.name);
    case 'consumers':
      return (a, b) => (b.consumers ?? 0) - (a.consumers ?? 0) || a.name.localeCompare(b.name);
    default:
      return (a, b) => a.vhost.localeCompare(b.vhost) || a.name.localeCompare(b.name);
  }
}
