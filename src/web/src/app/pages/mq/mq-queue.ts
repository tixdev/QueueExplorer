import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { Api } from '../../core/api';
import { ClusterStore } from '../../core/cluster-store';
import { errorMessage } from '../../core/http';
import { BrowseResponse, BrowsedMessage } from '../../core/models';
import { DateTimePipe, NumberPipe, RelativePipe } from '../../shared/format';
import { MessageList } from '../../shared/message-list';
import { MessageView } from '../../shared/message-view';
import { DepthMeter, QueueTypeLabels } from './mq';

/**
 * One IBM MQ queue: definition and status from the snapshot (no extra call), and a true browse of its messages.
 * Operations (move, copy, delete…) are not offered until they are implemented for IBM MQ.
 */
@Component({
  selector: 'app-mq-queue',
  imports: [RouterLink, NumberPipe, RelativePipe, DateTimePipe, DepthMeter, MessageList, MessageView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './mq-queue.html',
})
export class MqQueuePage {
  private readonly api = inject(Api);
  readonly store = inject(ClusterStore);
  readonly typeLabels = QueueTypeLabels;

  private readonly params = toSignal(
    inject(ActivatedRoute).paramMap.pipe(map((p) => ({ qmgr: p.get('vhost') ?? '', name: p.get('name') ?? '' }))),
    { requireSync: true },
  );
  readonly qmgr = computed(() => this.params().qmgr);
  readonly name = computed(() => this.params().name);
  readonly queue = computed(() => this.store.queue(this.qmgr(), this.name()));
  readonly now = computed(() => this.store.checkedAt() ?? Date.now());

  /** Alias: the target queue, when it is a queue of this queue manager we can link to. */
  readonly aliasTarget = computed(() => {
    const q = this.queue();
    return q?.type === 'alias' && q.target ? this.store.queue(this.qmgr(), q.target) : undefined;
  });
  /** Remote definition: the transmission queue its messages wait on. */
  readonly transmissionQueue = computed(() => {
    const q = this.queue();
    const xmitq = q?.type === 'remote' ? / via (\S+)$/.exec(q.target ?? '')?.[1] : undefined;
    return xmitq ? this.store.queue(this.qmgr(), xmitq) : undefined;
  });

  readonly count = signal(20);
  readonly browsing = signal(false);
  readonly browseError = signal<string | null>(null);
  readonly result = signal<BrowseResponse | null>(null);
  readonly selected = signal<BrowsedMessage | null>(null);
  readonly selectedKeys = signal<ReadonlySet<string>>(new Set());
  readonly filter = signal('');

  readonly messages = computed(() => {
    const list = this.result()?.messages ?? [];
    const f = this.filter().toLowerCase();
    if (!f) return list;
    return list.filter((m) =>
      [m.body.text, m.contentType, m.ibmMq?.messageIdText, m.ibmMq?.messageIdHex, m.ibmMq?.correlationIdText, m.ibmMq?.bodyFormat]
        .some((v) => v?.toLowerCase().includes(f)),
    );
  });

  constructor() {
    effect(() => {
      this.name();
      this.result.set(null);
      this.selected.set(null);
      this.selectedKeys.set(new Set());
      this.browseError.set(null);
    });
  }

  browse(): void {
    const cluster = this.store.cluster();
    if (!cluster) return;
    this.browsing.set(true);
    this.browseError.set(null);
    this.api.browse(cluster, this.qmgr(), this.name(), this.count(), false).subscribe({
      next: (r) => {
        this.browsing.set(false);
        this.result.set(r);
        this.selected.set(r.messages[0] ?? null);
        this.selectedKeys.set(new Set());
      },
      error: (e: unknown) => {
        this.browsing.set(false);
        this.browseError.set(errorMessage(e));
      },
    });
  }

  oldestAge(seconds: number): string {
    if (seconds < 60) return `${seconds} s`;
    if (seconds < 3600) return `${Math.round(seconds / 60)} min`;
    if (seconds < 86400) return `${(seconds / 3600).toFixed(1)} h`;
    return `${(seconds / 86400).toFixed(1)} days`;
  }
}
