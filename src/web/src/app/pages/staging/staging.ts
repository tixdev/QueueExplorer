import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable } from 'rxjs';
import { Api } from '../../core/api';
import { Auth } from '../../core/auth';
import { ClusterStore } from '../../core/cluster-store';
import { errorMessage } from '../../core/http';
import { Actions, Brokers, BrowsedMessage, OperationFailure, StagingBatch, StagingBatchDetail, brokerLabel } from '../../core/models';
import { DestinationChoice, DestinationDialog, retryTargets } from '../../shared/destination-dialog';
import { DateTimePipe } from '../../shared/format';
import { persistedSignal } from '../../shared/list-filters';
import { MessageEditor } from '../../shared/message-editor';
import { MessageList } from '../../shared/message-list';
import { MessageView } from '../../shared/message-view';

@Component({
  selector: 'app-staging',
  imports: [DateTimePipe, MessageList, MessageView, MessageEditor, DestinationDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './staging.html',
})
export class StagingPage {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly store = inject(ClusterStore);

  private readonly query = toSignal(this.route.queryParamMap);

  readonly brokers = Brokers;
  readonly brokerLabel = brokerLabel;
  /** Empty: all brokers. */
  readonly broker = persistedSignal('staging.broker', '');

  private readonly allBatches = signal<StagingBatch[]>([]);
  readonly batches = computed(() => this.allBatches().filter((b) => !this.broker() || b.broker === this.broker()));
  readonly brokerCounts = computed(() => {
    const counts = new Map<string, number>();
    for (const b of this.allBatches()) counts.set(b.broker, (counts.get(b.broker) ?? 0) + 1);
    return counts;
  });

  readonly detail = signal<StagingBatchDetail | null>(null);
  readonly selectedKeys = signal<ReadonlySet<string>>(new Set());
  readonly current = signal<BrowsedMessage | null>(null);
  readonly editing = signal(false);
  readonly showDiscarded = signal(false);
  readonly publishing = signal(false);
  readonly working = signal(false);
  readonly error = signal<string | null>(null);
  readonly outcome = signal<{ text: string; failures: OperationFailure[] } | null>(null);

  readonly canWrite = computed(() => this.auth.canGlobal(Actions.stagingWrite));
  readonly canPublish = computed(() => this.auth.can(this.store.cluster() ?? '', Actions.messagesPublish));
  readonly batchId = computed(() => Number(this.query()?.get('batch')) || null);
  /** Publishing and editing are implemented for RabbitMQ messages only. */
  readonly isRabbit = computed(() => this.detail()?.batch.broker === 'rabbitmq');

  readonly messages = computed(() => (this.detail()?.messages ?? []).filter((m) => this.showDiscarded() || !m.discarded));
  readonly hasFailedMessages = computed(() => this.messages().some((m) => m.nServiceBus?.kind === 'failedMessage'));
  readonly scopeMessages = computed(() => {
    const keys = this.selectedKeys();
    const all = this.detail()?.messages ?? [];
    return keys.size ? all.filter((m) => keys.has(String(m.stagedId))) : all.filter((m) => !m.discarded);
  });
  readonly retryTargets = computed(() => retryTargets(this.scopeMessages()));
  readonly selectedIds = computed(() => [...this.selectedKeys()].map(Number));
  readonly scopeLabel = computed(() => (this.selectedKeys().size ? `${this.selectedKeys().size} selected` : 'all messages not discarded'));

  constructor() {
    this.loadBatches();
    effect(() => {
      const id = this.batchId();
      this.selectedKeys.set(new Set());
      this.current.set(null);
      this.editing.set(false);
      this.outcome.set(null);
      if (id) this.loadDetail(id);
      else this.detail.set(null);
    });
  }

  open(batch: StagingBatch): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { batch: batch.id } });
  }

  select(message: BrowsedMessage | null): void {
    this.current.set(message);
    this.editing.set(false);
  }

  import(input: HTMLInputElement): void {
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.run(this.api.importToStaging(file), (b) => {
      this.loadBatches();
      this.open(b);
      return { text: `Imported ${b.messages} messages from ${file.name}.`, failures: [] };
    });
  }

  edited(updated: BrowsedMessage): void {
    this.editing.set(false);
    this.detail.update((d) => d && { ...d, messages: d.messages.map((m) => (m.stagedId === updated.stagedId ? { ...updated, index: m.index } : m)) });
    this.current.set(this.detail()?.messages.find((m) => m.stagedId === updated.stagedId) ?? null);
    this.outcome.set({ text: updated.editedAt ? 'Message saved.' : 'Original message restored.', failures: [] });
  }

  publish(choice: DestinationChoice): void {
    this.publishing.set(false);
    const id = this.batchId()!;
    this.run(
      this.api.publishStaged(id, {
        cluster: this.store.cluster()!,
        vhost: choice.vhost,
        queue: choice.queue,
        retryToFailedQueue: choice.retryToFailedQueue,
        removeFailureHeaders: choice.removeFailureHeaders,
        ids: this.selectedKeys().size ? this.selectedIds() : undefined,
      }),
      (r) => {
        this.reload();
        return {
          text: `Published ${r.published} of ${r.requested} messages${choice.queue ? ` to ${this.store.cluster()}/${choice.vhost}/${choice.queue}` : ' to their failed queues'}. The messages stay in staging.`,
          failures: r.failures,
        };
      },
    );
  }

  discard(discarded: boolean): void {
    this.run(this.api.discardStaged(this.batchId()!, this.selectedIds(), discarded), () => {
      this.reload();
      this.selectedKeys.set(new Set());
      return { text: discarded ? 'Messages discarded: they will not be published or exported.' : 'Messages restored.', failures: [] };
    });
  }

  exportBatch(): void {
    this.run(this.api.exportStagingBatch(this.batchId()!), () => ({ text: '.mqx file downloaded.', failures: [] }));
  }

  deleteBatch(): void {
    const batch = this.detail()?.batch;
    if (!batch || !confirm(`Delete the batch “${batch.name}” with ${batch.messages} messages?`)) return;
    this.run(this.api.deleteStagingBatch(batch.id), () => {
      this.loadBatches();
      void this.router.navigate([], { relativeTo: this.route, queryParams: {} });
      return { text: 'Batch deleted.', failures: [] };
    });
  }

  private reload(): void {
    this.loadBatches();
    const id = this.batchId();
    if (id) this.loadDetail(id);
  }

  private loadBatches(): void {
    this.api.stagingBatches().subscribe({ next: (b) => this.allBatches.set(b), error: (e) => this.error.set(errorMessage(e)) });
  }

  private loadDetail(id: number): void {
    this.api.stagingBatch(id).subscribe({
      next: (d) => {
        this.detail.set(d);
        const currentId = this.current()?.stagedId;
        this.current.set(d.messages.find((m) => m.stagedId === currentId) ?? d.messages[0] ?? null);
      },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  private run<T>(operation: Observable<T>, describe: (r: T) => { text: string; failures: OperationFailure[] }): void {
    this.working.set(true);
    this.error.set(null);
    this.outcome.set(null);
    operation.subscribe({
      next: (r) => {
        this.working.set(false);
        this.outcome.set(describe(r));
      },
      error: (e: unknown) => {
        this.working.set(false);
        this.error.set(errorMessage(e));
      },
    });
  }
}
