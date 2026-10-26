import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { Api, QUORUM_CONFIRMATION } from '../../core/api';
import { ClusterStore } from '../../core/cluster-store';
import { errorMessage } from '../../core/http';
import { BrowseResponse, BrowsedMessage, QueueDetail } from '../../core/models';
import { JsonValuePipe, NumberPipe } from '../../shared/format';
import { MessageView } from '../../shared/message-view';
import { MessageList } from '../../shared/message-list';
import { DestinationChoice, DestinationDialog, retryTargets } from '../../shared/destination-dialog';
import { FlowLinks } from '../../shared/flow-links';
import { Auth } from '../../core/auth';
import { Actions, OperationFailure } from '../../core/models';

@Component({
  selector: 'app-queue',
  imports: [RouterLink, NumberPipe, JsonValuePipe, MessageView, MessageList, DestinationDialog, FlowLinks],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './queue.html',
})
export class QueuePage {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  readonly store = inject(ClusterStore);

  private readonly params = toSignal(
    inject(ActivatedRoute).paramMap.pipe(map((p) => ({ vhost: p.get('vhost') ?? '', name: p.get('name') ?? '' }))),
    { requireSync: true },
  );
  readonly vhost = computed(() => this.params().vhost);
  readonly name = computed(() => this.params().name);

  /** Instant: comes from the shared topology snapshot. */
  readonly queue = computed(() => this.store.queue(this.vhost(), this.name()));
  readonly bindings = computed(() => this.store.bindingsTo('queue', this.vhost(), this.name()));
  readonly argumentEntries = computed(() => Object.entries(this.queue()?.arguments ?? {}));

  readonly detail = signal<QueueDetail | null>(null);

  readonly count = signal(20);
  readonly browsing = signal(false);
  /** Delivery limit reported by the API when it asks for confirmation. */
  readonly confirmLimit = signal<number | null>(null);
  readonly browseError = signal<string | null>(null);
  readonly result = signal<BrowseResponse | null>(null);
  readonly selected = signal<BrowsedMessage | null>(null);
  readonly filter = signal('');
  readonly selectedKeys = signal<ReadonlySet<string>>(new Set());

  readonly dialog = signal<'move' | 'copy' | 'retry' | null>(null);
  readonly confirmDelete = signal(false);
  readonly purging = signal(false);
  readonly purgeName = signal('');
  readonly working = signal(false);
  readonly outcome = signal<{ text: string; failures: OperationFailure[]; stagingBatch?: number } | null>(null);

  readonly can = computed(() => {
    const cluster = this.store.cluster() ?? '';
    return {
      move: this.auth.can(cluster, Actions.messagesMove),
      publish: this.auth.can(cluster, Actions.messagesPublish),
      export: this.auth.can(cluster, Actions.messagesExport),
      stage: this.auth.canGlobal(Actions.stagingWrite),
      delete: this.auth.can(cluster, Actions.messagesDelete),
      purge: this.auth.can(cluster, Actions.queuesPurge),
    };
  });
  /** Messages the next operation applies to, as far as the browse result shows them. */
  readonly scopeMessages = computed(() => {
    const browsed = this.result()?.messages ?? [];
    const keys = this.selectedKeys();
    return keys.size ? browsed.filter((m) => keys.has(m.key)) : browsed.slice(0, this.count());
  });
  readonly retryTargets = computed(() => retryTargets(this.scopeMessages()));
  readonly retryTargetsNote = computed(() =>
    !this.selectedKeys().size && (this.result()?.queueDepth ?? 0) > this.scopeMessages().length
      ? `Based on the ${this.scopeMessages().length} messages read (the first of the queue):`
      : '',
  );
  readonly hasFailedMessages = computed(() => (this.result()?.messages ?? []).some((m) => m.nServiceBus?.kind === 'failedMessage'));
  /** Operations apply to the selected messages or, when none is selected, to the first N of the queue. */
  readonly scopeLabel = computed(() => (this.selectedKeys().size ? `${this.selectedKeys().size} selected` : `the first ${this.count()}`));

  readonly messages = computed(() => {
    const list = this.result()?.messages ?? [];
    const f = this.filter().toLowerCase();
    if (!f) return list;
    return list.filter((m) =>
      [m.nServiceBus?.messageTypes[0]?.name, m.nServiceBus?.failure?.exceptionType, m.nServiceBus?.failure?.message, m.nServiceBus?.messageId, m.body.text]
        .some((v) => v?.toLowerCase().includes(f)),
    );
  });

  constructor() {
    // Live rates for this queue: one cheap call when the page opens.
    effect(() => {
      const cluster = this.store.cluster();
      const vhost = this.vhost();
      const name = this.name();
      this.detail.set(null);
      this.result.set(null);
      this.selected.set(null);
      this.selectedKeys.set(new Set());
      this.outcome.set(null);
      if (!cluster) return;
      this.api.queue(cluster, vhost, name).subscribe({ next: (d) => this.detail.set(d), error: () => this.detail.set(null) });
    });
  }

  browse(accept = false): void {
    const cluster = this.store.cluster();
    if (!cluster) return;
    this.browsing.set(true);
    this.browseError.set(null);
    this.confirmLimit.set(null);
    this.api.browse(cluster, this.vhost(), this.name(), this.count(), accept).subscribe({
      next: (r) => {
        this.browsing.set(false);
        this.result.set(r);
        this.selected.set(r.messages[0] ?? null);
        this.selectedKeys.set(new Set());
      },
      error: (e: unknown) => {
        this.browsing.set(false);
        const problem = e instanceof HttpErrorResponse ? (e.error as { type?: string; deliveryLimit?: number } | null) : null;
        if (e instanceof HttpErrorResponse && e.status === 409 && problem?.type === QUORUM_CONFIRMATION) {
          this.confirmLimit.set(problem.deliveryLimit ?? 0);
          return;
        }
        this.browseError.set(errorMessage(e));
      },
    });
  }

  deliveryLimitLabel(limit: number | undefined): string {
    if (limit === undefined || limit === null) return '–';
    return limit < 0 ? 'unlimited' : limit >= 10000 ? `${limit} (practically unlimited)` : String(limit);
  }

  transfer(choice: DestinationChoice): void {
    const mode = this.dialog() === 'copy' ? 'copy' : 'move';
    this.dialog.set(null);
    this.run(
      this.api.transfer(this.store.cluster()!, {
        vhost: this.vhost(),
        queue: this.name(),
        mode,
        ...this.scope(),
        destinationVhost: choice.vhost,
        destinationQueue: choice.queue,
        retryToFailedQueue: choice.retryToFailedQueue,
        removeFailureHeaders: choice.removeFailureHeaders,
      }),
      (o) => ({
        text: `${choice.retryToFailedQueue ? 'Sent back to their failed queue' : mode === 'move' ? 'Moved' : 'Copied'} ${o.succeeded} of ${o.selected} messages` +
          (choice.queue ? ` to ${choice.vhost}/${choice.queue}` : '') + '.',
        failures: o.failures,
      }),
      mode === 'move',
    );
  }

  exportFile(): void {
    const { keys, count } = this.scope();
    this.run(this.api.exportMessages(this.store.cluster()!, this.vhost(), this.name(), keys, count), () => ({
      text: '.mqx file downloaded. The messages are still in the queue.',
      failures: [],
    }), false);
  }

  stage(remove: boolean): void {
    const { keys, count } = this.scope();
    this.run(this.api.stage(this.store.cluster()!, this.vhost(), this.name(), remove, keys, count), (b) => ({
      text: `${b.messages} messages ${remove ? 'moved' : 'copied'} to staging (“${b.name}”).`,
      failures: [],
      stagingBatch: b.id,
    }), remove);
  }

  deleteMessages(): void {
    this.confirmDelete.set(false);
    const { keys, count } = this.scope();
    this.run(this.api.deleteMessages(this.store.cluster()!, this.vhost(), this.name(), keys, count), (r) => ({
      text: `Deleted ${r.deleted} message${r.deleted === 1 ? '' : 's'}.`,
      failures: [],
    }), true);
  }

  purge(): void {
    const name = this.purgeName();
    this.purging.set(false);
    this.purgeName.set('');
    this.run(this.api.purgeQueue(this.store.cluster()!, this.vhost(), this.name(), name), (r) => ({
      text: `Queue purged${r.messagesBefore !== undefined && r.messagesBefore !== null ? ` (${r.messagesBefore} messages removed)` : ''}.`,
      failures: [],
    }), true);
  }

  private scope(): { keys?: string[]; count?: number } {
    return this.selectedKeys().size ? { keys: [...this.selectedKeys()] } : { count: this.count() };
  }

  /** Runs an operation; when it changed the queue, the browse result is stale and is cleared. */
  private run<T>(operation: import('rxjs').Observable<T>, describe: (r: T) => { text: string; failures: OperationFailure[]; stagingBatch?: number }, changesQueue: boolean): void {
    this.working.set(true);
    this.outcome.set(null);
    this.browseError.set(null);
    operation.subscribe({
      next: (r) => {
        this.working.set(false);
        this.outcome.set(describe(r));
        if (changesQueue) {
          this.result.set(null);
          this.selected.set(null);
          this.selectedKeys.set(new Set());
          this.store.refresh();
        }
      },
      error: (e: unknown) => {
        this.working.set(false);
        this.browseError.set(errorMessage(e));
      },
    });
  }
}

