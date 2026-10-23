import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import { ClusterStore } from '../core/cluster-store';
import { isNsbInfrastructure } from './format';
import { matchesSearch } from './list-filters';

/** Where "back to FailedQ" sends the messages in scope, grouped by queue (null: no FailedQ header). */
export interface RetryTarget {
  queue: string | null;
  count: number;
}

/** Groups messages by their NServiceBus FailedQ, most common first. */
export function retryTargets(messages: { nServiceBus?: { failure?: { failedQueue?: string } } }[]): RetryTarget[] {
  const counts = new Map<string | null, number>();
  for (const m of messages) {
    const queue = m.nServiceBus?.failure?.failedQueue ?? null;
    counts.set(queue, (counts.get(queue) ?? 0) + 1);
  }
  return [...counts].map(([queue, count]) => ({ queue, count })).sort((a, b) => b.count - a.count);
}

export interface DestinationChoice {
  vhost: string;
  queue?: string;
  retryToFailedQueue: boolean;
  removeFailureHeaders: boolean;
}

/** Picks a destination queue from the cluster topology, or "back to FailedQ" for failed NServiceBus messages. */
@Component({
  selector: 'app-destination-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="overlay" (click)="cancel.emit()">
      <div class="card dialog" (click)="$event.stopPropagation()">
        <h3>{{ title() }}</h3>
        <p class="muted">{{ description() }}</p>

        @if (allowRetry()) {
          <label class="check"><input type="radio" name="target" [checked]="!retry()" (change)="retry.set(false)" /> A queue of your choice</label>
          <label class="check"><input type="radio" name="target" [checked]="retry()" (change)="retry.set(true)" />
            Back to each message's failed queue (<span class="mono">NServiceBus.FailedQ</span>, a retry as ServiceControl does)</label>
        }

        @if (!retry()) {
          <div class="toolbar">
            <select (change)="setVhost($any($event.target).value)">
              @if (vhostsOutsideGroup().length) {
                <optgroup [label]="'In ' + groupName()">
                  @for (v of vhostsInGroup(); track v) {
                    <option [value]="v" [selected]="v === vhost()">{{ v }}</option>
                  }
                </optgroup>
                <optgroup label="Other vhosts">
                  @for (v of vhostsOutsideGroup(); track v) {
                    <option [value]="v" [selected]="v === vhost()">{{ v }}</option>
                  }
                </optgroup>
              } @else {
                @for (v of vhostsInGroup(); track v) {
                  <option [value]="v" [selected]="v === vhost()">{{ v }}</option>
                }
              }
            </select>
            <input class="search" type="search" placeholder="Search queues…" [value]="search()" (input)="search.set($any($event.target).value)" />
          </div>
          <ul class="pick-list">
            @for (q of queues(); track q) {
              <li [class.selected]="q === queue()" (click)="queue.set(q)"><span class="mono">{{ q }}</span></li>
            } @empty {
              <li class="muted">No queues</li>
            }
          </ul>
        } @else {
          @if (retryTargets().length) {
            <div class="retry-targets">
              <span class="muted small">{{ retryTargetsNote() || 'Where each message goes:' }}</span>
              <ul>
                @for (t of retryTargets(); track t.queue) {
                  @if (t.queue) {
                    <li><span class="count-pill">{{ t.count }}</span> → <span class="mono">{{ initialVhost() }}/{{ t.queue }}</span></li>
                  } @else {
                    <li class="warn-text"><span class="count-pill">{{ t.count }}</span> without <span class="mono">FailedQ</span>: they stay in the current queue</li>
                  }
                }
              </ul>
            </div>
          }
          <label class="check"><input type="checkbox" [checked]="removeFailureHeaders()" (change)="removeFailureHeaders.set($any($event.target).checked)" />
            Remove the failure headers (exception, FailedQ, retries)</label>
        }

        <div class="dialog-actions">
          <button (click)="cancel.emit()">Cancel</button>
          <button class="primary" [disabled]="!retry() && !queue()" (click)="confirm()">{{ confirmLabel() }}</button>
        </div>
      </div>
    </div>
  `,
})
export class DestinationDialog {
  private readonly store = inject(ClusterStore);

  readonly title = input.required<string>();
  readonly description = input('');
  readonly confirmLabel = input('Confirm');
  readonly allowRetry = input(false);
  /** Start with "back to FailedQ" selected (the Retry action) instead of a queue of choice. */
  readonly preferRetry = input(false);
  readonly initialVhost = input<string>('');
  /** Destinations of the messages in scope when sending them back to their FailedQ. */
  readonly retryTargets = input<RetryTarget[]>([]);
  /** Caveat shown above the targets, e.g. when they are derived from the browsed messages only. */
  readonly retryTargetsNote = input('');
  /** Queue to leave out (the source). */
  readonly exclude = input<{ vhost: string; queue: string } | null>(null);

  readonly chosen = output<DestinationChoice>();
  readonly cancel = output<void>();

  readonly retry = linkedSignal(() => this.allowRetry() && this.preferRetry());
  readonly removeFailureHeaders = signal(true);
  readonly search = signal('');
  readonly queue = signal<string | null>(null);
  private readonly selectedVhost = signal<string | null>(null);

  // The selected queue group comes first, but any vhost can be chosen: moving out of a group is deliberate, not blocked.
  readonly groupName = computed(() => this.store.group()?.group.name ?? '');
  readonly vhostsInGroup = computed(() => this.store.topology()?.vhosts ?? []);
  readonly vhostsOutsideGroup = computed(() => {
    if (!this.store.group()) return [];
    const inGroup = new Set(this.vhostsInGroup());
    return (this.store.fullTopology()?.vhosts ?? []).filter((v) => !inGroup.has(v));
  });
  readonly vhost = computed(() => this.selectedVhost() ?? (this.initialVhost() || this.vhostsInGroup()[0] || this.vhostsOutsideGroup()[0] || ''));

  readonly queues = computed(() => {
    const vhost = this.vhost();
    const exclude = this.exclude();
    // Group members first within the vhost; for a rule on queue names (IBM MQ) the others follow.
    return (this.store.fullTopology()?.queues ?? [])
      .filter((q) => q.vhost === vhost && !isNsbInfrastructure(q.name) && !(exclude && exclude.vhost === q.vhost && exclude.queue === q.name))
      .map((q) => q.name)
      .filter((name) => matchesSearch(name, this.search()))
      .sort((a, b) => Number(this.store.inGroup(vhost, b)) - Number(this.store.inGroup(vhost, a)) || a.localeCompare(b));
  });

  setVhost(vhost: string): void {
    this.selectedVhost.set(vhost);
    this.queue.set(null);
  }

  confirm(): void {
    this.chosen.emit({
      vhost: this.vhost(),
      queue: this.retry() ? undefined : (this.queue() ?? undefined),
      retryToFailedQueue: this.retry(),
      removeFailureHeaders: this.retry() && this.removeFailureHeaders(),
    });
  }
}
