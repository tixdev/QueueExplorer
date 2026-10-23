import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { Auth } from '../../core/auth';
import { ClusterStore } from '../../core/cluster-store';
import { Actions } from '../../core/models';
import { RelativePipe } from '../../shared/format';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, RelativePipe],
  providers: [ClusterStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
})
export class Shell {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  readonly store = inject(ClusterStore);

  readonly me = this.auth.me;
  readonly cluster = toSignal(inject(ActivatedRoute).paramMap.pipe(map((p) => p.get('cluster') ?? '')), { initialValue: '' });
  readonly actions = computed(() => this.me()?.clusters.find((c) => c.name === this.cluster())?.actions ?? []);
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
