import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ClusterStore } from '../../core/cluster-store';
import { Exchange } from '../../core/models';
import { NumberPipe, isNsbInfrastructure } from '../../shared/format';
import { matchesSearch, persistedSignal } from '../../shared/list-filters';

@Component({
  selector: 'app-exchanges',
  imports: [ScrollingModule, RouterLink, NumberPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './exchanges.html',
})
export class ExchangesPage {
  readonly store = inject(ClusterStore);
  private readonly query = toSignal(inject(ActivatedRoute).queryParamMap);

  readonly vhost = persistedSignal('exchanges.vhost', '');
  readonly search = persistedSignal('exchanges.search', '');
  readonly hideInfrastructure = persistedSignal('exchanges.hideInfra', true);
  readonly selected = signal<{ vhost: string; name: string } | null>(null);

  readonly vhosts = computed(() => this.store.topology()?.vhosts ?? []);

  readonly exchanges = computed(() => {
    const vhost = this.vhost();
    const search = this.search().trim();
    const hide = this.hideInfrastructure();
    return (this.store.topology()?.exchanges ?? [])
      .filter((e) => (!vhost || e.vhost === vhost) && (!hide || !isBuiltInOrInfrastructure(e)) && matchesSearch(e.name, search))
      .sort((a, b) => a.vhost.localeCompare(b.vhost) || a.name.localeCompare(b.name));
  });

  readonly current = computed(() => {
    const s = this.selected();
    return s ? this.store.exchange(s.vhost, s.name) : undefined;
  });
  // Both directions come from the indexes built once per snapshot: no request per exchange.
  readonly outgoing = computed(() => {
    const s = this.selected();
    return s ? this.store.bindingsFrom(s.vhost, s.name) : [];
  });
  readonly incoming = computed(() => {
    const s = this.selected();
    return s ? this.store.bindingsTo('exchange', s.vhost, s.name) : [];
  });

  constructor() {
    // Deep link from a queue's binding list: /exchanges?vhost=...&name=...
    effect(() => {
      const q = this.query();
      const vhost = q?.get('vhost');
      const name = q?.get('name');
      if (vhost !== null && vhost !== undefined && name) {
        this.selected.set({ vhost, name });
        this.vhost.set(vhost);
      }
    });
  }

  select(e: Exchange): void {
    this.selected.set({ vhost: e.vhost, name: e.name });
  }

  track(_: number, e: Exchange): string {
    return e.vhost + '/' + e.name;
  }

  displayName(name: string): string {
    return name || '(default)';
  }
}

function isBuiltInOrInfrastructure(e: Exchange): boolean {
  return e.name === '' || e.name.startsWith('amq.') || isNsbInfrastructure(e.name);
}
