import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { Auth } from '../../core/auth';
import { ClusterStore } from '../../core/cluster-store';
import { Actions, Brokers, brokerLabel } from '../../core/models';
import { QueueGroups } from '../../core/queue-groups';
import { NumberPipe, RelativePipe } from '../../shared/format';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, RelativePipe, NumberPipe],
  providers: [ClusterStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
})
export class Shell {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  readonly store = inject(ClusterStore);
  readonly queueGroups = inject(QueueGroups);

  readonly me = this.auth.me;
  readonly cluster = toSignal(inject(ActivatedRoute).paramMap.pipe(map((p) => p.get('cluster') ?? '')), { initialValue: '' });
  readonly current = computed(() => this.me()?.clusters.find((c) => c.name === this.cluster()));
  readonly actions = computed(() => this.current()?.actions ?? []);
  /** Connections grouped by broker, in the order of the broker list. */
  readonly connectionGroups = computed(() => {
    const clusters = this.me()?.clusters ?? [];
    return Brokers.map((b) => ({ broker: b.id, label: b.label, connections: clusters.filter((c) => c.broker === b.id) })).filter((g) => g.connections.length);
  });
  readonly brokerLabel = brokerLabel;
  readonly canAudit = computed(() => this.me()?.globalActions.includes(Actions.auditRead) ?? false);
  readonly canStaging = computed(() => this.me()?.globalActions.includes(Actions.stagingRead) ?? false);
  readonly checkedAtIso = computed(() => {
    const at = this.store.checkedAt();
    return at === null ? null : new Date(at).toISOString();
  });
  readonly A = Actions;

  // Keeps the relative "updated x seconds ago" label moving.
  readonly now = signal(Date.now());

  constructor() {
    effect(() => {
      const cluster = this.cluster();
      if (cluster) this.store.select(cluster);
    });
    const timer = setInterval(() => this.now.set(Date.now()), 5000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  can(action: string): boolean {
    return this.actions().includes(action);
  }

  switchCluster(name: string): void {
    void this.router.navigate(['/c', name]);
  }

  refresh(): void {
    this.store.refresh();
  }

  logout(): void {
    this.auth.logout().subscribe(() => void this.router.navigate(['/login']));
  }
}
