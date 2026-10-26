import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { Api } from '../../core/api';
import { ClusterStore } from '../../core/cluster-store';
import { errorMessage } from '../../core/http';
import { BrowseResponse, BrowsedMessage } from '../../core/models';
import { BytesPipe, NumberPipe } from '../../shared/format';
import { FlowLinks } from '../../shared/flow-links';
import { MessageList } from '../../shared/message-list';
import { MessageView } from '../../shared/message-view';
import { KindLabels, isStuckOutgoing, messagesLabel, shortName } from './msmq';

/**
 * One MSMQ queue: status from the snapshot (no extra call) and a peek of its messages. Operations (move, retry,
 * delete…) are not offered until they are implemented for MSMQ.
 */
@Component({
  selector: 'app-msmq-queue',
  imports: [RouterLink, NumberPipe, BytesPipe, MessageList, MessageView, FlowLinks],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './msmq-queue.html',
})
export class MsmqQueuePage {
  private readonly api = inject(Api);
  readonly store = inject(ClusterStore);
  readonly kindLabels = KindLabels;
  readonly shortName = shortName;
  readonly messagesLabel = messagesLabel;
  readonly isStuck = isStuckOutgoing;

  private readonly params = toSignal(
    inject(ActivatedRoute).paramMap.pipe(map((p) => ({ machine: p.get('vhost') ?? '', name: p.get('name') ?? '' }))),
    { requireSync: true },
  );
  readonly machine = computed(() => this.params().machine);
  readonly name = computed(() => this.params().name);
  readonly queue = computed(() => this.store.queue(this.machine(), this.name()));

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
      [m.body.text, m.msmq?.label, m.msmq?.id, m.msmq?.classDescription, m.nServiceBus?.messageTypes[0]?.fullName, m.nServiceBus?.failure?.exceptionType]
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
    this.api.browse(cluster, this.machine(), this.name(), this.count(), false).subscribe({
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
}
