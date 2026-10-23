import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { EMPTY } from 'rxjs';
import { Api } from '../../core/api';
import { ClusterStore } from '../../core/cluster-store';
import { Shovel } from '../../core/models';
import { poll } from '../../core/polling';
import { matchesSearch } from '../../shared/list-filters';

@Component({
  selector: 'app-shovels',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shovels.html',
})
export class ShovelsPage {
  private readonly api = inject(Api);
  private readonly store = inject(ClusterStore);

  private readonly polled = poll(() => {
    const cluster = this.store.cluster();
    return cluster ? this.api.shovels(cluster) : EMPTY;
  }, 5000);

  readonly error = this.polled.error;
  readonly search = signal('');
  readonly onlyProblems = signal(false);
  readonly expanded = signal<string | null>(null);

  readonly shovels = computed(() =>
    (this.polled.value() ?? []).filter(
      (s) =>
        (!this.onlyProblems() || s.state !== 'running') &&
        matchesSearch(`${s.vhost ?? ''} ${s.name} ${s.source ?? ''} ${s.destination ?? ''}`, this.search()),
    ),
  );
  readonly problems = computed(() => (this.polled.value() ?? []).filter((s) => s.state !== 'running').length);
  readonly loaded = computed(() => this.polled.value() !== undefined);

  key(s: Shovel): string {
    return (s.vhost ?? '') + '/' + s.name;
  }

  stateClass(state: string): string {
    return state === 'running' ? 'ok' : state === 'starting' ? 'warn' : 'danger';
  }

  definition(s: Shovel): string {
    return JSON.stringify(s.definition ?? {}, null, 2);
  }
}
