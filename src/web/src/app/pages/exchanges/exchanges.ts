import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ClusterStore } from '../../core/cluster-store';
import { Exchange } from '../../core/models';
import { EventFlow } from '../../shared/event-flow';
import { FlowLinks } from '../../shared/flow-links';
import { NumberPipe } from '../../shared/format';
import { matchesSearch, persistedSignal } from '../../shared/list-filters';
import { ExchangeKind, eventName, exchangeKind, subscribersOf, subscriptionsOf } from '../../shared/nsb-topology';

type KindFilter = 'all' | 'event' | 'endpoint' | 'other';

interface Row {
  exchange: Exchange;
  kind: ExchangeKind;
  /** Subscribers for events, bindings otherwise. */
  links: number;
}

@Component({
  selector: 'app-exchanges',
  imports: [ScrollingModule, RouterLink, NumberPipe, EventFlow, FlowLinks],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './exchanges.html',
})
export class ExchangesPage {
  readonly store = inject(ClusterStore);
  private readonly query = toSignal(inject(ActivatedRoute).queryParamMap);

  readonly vhost = persistedSignal('exchanges.vhost', '');
  readonly search = persistedSignal('exchanges.search', '');
  readonly kind = persistedSignal<KindFilter>('exchanges.kind', 'all');
  readonly hideInfrastructure = persistedSignal('exchanges.hideInfra', true);
  readonly selected = signal<{ vhost: string; name: string } | null>(null);

  readonly vhosts = computed(() => this.store.topology()?.vhosts ?? []);
  /** The chosen vhost, unless the queue group left it out: then every vhost of the group. */
  readonly effectiveVhost = computed(() => (this.vhosts().includes(this.vhost()) ? this.vhost() : ''));
  private readonly from = (vhost: string, source: string) => this.store.bindingsFrom(vhost, source);
  private readonly queueOf = (vhost: string, name: string) => this.store.queue(vhost, name);

  /** Kind and link count of every exchange, computed once per topology snapshot. */
  private readonly rows = computed<Row[]>(() =>
    (this.store.topology()?.exchanges ?? []).map((e) => {
      const kind = exchangeKind(e, this.from);
      const links = kind === 'event' ? subscribersOf(e.vhost, e.name, this.from, this.queueOf).length : this.store.bindingsFrom(e.vhost, e.name).length;
      return { exchange: e, kind, links };
    }),
  );

  readonly counts = computed(() => {
    const vhost = this.effectiveVhost();
    const c = { all: 0, event: 0, endpoint: 0, other: 0 };
    for (const r of this.rows()) {
      if (vhost && r.exchange.vhost !== vhost) continue;
      if (this.hideInfrastructure() && (r.kind === 'builtin' || r.kind === 'delay')) continue;
      c.all++;
      if (r.kind === 'event' || r.kind === 'endpoint') c[r.kind]++;
      else c.other++;
    }
    return c;
  });

  readonly exchanges = computed(() => {
    const vhost = this.effectiveVhost();
    const search = this.search().trim();
    const kind = this.kind();
    const hide = this.hideInfrastructure();
    return this.rows()
      .filter((r) => (!vhost || r.exchange.vhost === vhost) && (!hide || (r.kind !== 'builtin' && r.kind !== 'delay')))
      .filter((r) => kind === 'all' || (kind === 'other' ? r.kind !== 'event' && r.kind !== 'endpoint' : r.kind === kind))
      .filter((r) => matchesSearch(r.exchange.name, search))
      .sort((a, b) => a.exchange.vhost.localeCompare(b.exchange.vhost) || a.exchange.name.localeCompare(b.exchange.name));
  });

  readonly current = computed(() => {
    const s = this.selected();
    const e = s ? this.store.exchange(s.vhost, s.name) : undefined;
    return e ? { exchange: e, kind: exchangeKind(e, this.from) } : undefined;
  });
  readonly subscribers = computed(() => {
    const c = this.current();
    return c?.kind === 'event' ? subscribersOf(c.exchange.vhost, c.exchange.name, this.from, this.queueOf) : [];
  });
  readonly subscriptions = computed(() => {
    const c = this.current();
    return c?.kind === 'endpoint' ? subscriptionsOf(c.exchange.vhost, c.exchange.name, (t, v, n) => this.store.bindingsTo(t, v, n)) : [];
  });
  readonly endpointQueue = computed(() => {
    const c = this.current();
    return c?.kind === 'endpoint' ? this.store.queue(c.exchange.vhost, c.exchange.name) : undefined;
  });
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

  openEvent(name: string): void {
    const c = this.current();
    if (c) this.selected.set({ vhost: c.exchange.vhost, name });
  }

  track(_: number, r: Row): string {
    return r.exchange.vhost + '/' + r.exchange.name;
  }

  displayName(name: string): string {
    return name || '(default)';
  }

  eventName = eventName;

  kindLabel(kind: ExchangeKind): string {
    return { event: 'Event', endpoint: 'Endpoint', delay: 'Delay', builtin: 'Built-in', other: 'Exchange' }[kind];
  }
}
