import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Api } from '../../core/api';
import { ClusterStore } from '../../core/cluster-store';
import { RmqNode } from '../../core/models';
import { poll } from '../../core/polling';
import { BytesPipe, NumberPipe, UptimePipe } from '../../shared/format';
import { EMPTY } from 'rxjs';

interface Gauge {
  label: string;
  used?: number;
  limit?: number;
  alarm?: boolean;
  bytes: boolean;
  /** For disk the limit is a floor, not a ceiling. */
  inverse?: boolean;
}

@Component({
  selector: 'app-overview',
  imports: [NumberPipe, BytesPipe, UptimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overview.html',
})
export class OverviewPage {
  private readonly api = inject(Api);
  private readonly store = inject(ClusterStore);

  private readonly polled = poll(() => {
    const cluster = this.store.cluster();
    return cluster ? this.api.overview(cluster) : EMPTY;
  }, 5000);

  readonly data = this.polled.value;
  readonly error = this.polled.error;
  readonly overview = computed(() => this.data()?.overview);
  readonly nodes = computed(() => this.data()?.nodes);

  gauges(n: RmqNode): Gauge[] {
    const gauges: Gauge[] = [
      { label: 'Memory', used: n.memUsed, limit: n.memLimit, alarm: n.memAlarm, bytes: true },
      { label: 'Free disk', used: n.diskFree, limit: n.diskFreeLimit, alarm: n.diskFreeAlarm, bytes: true, inverse: true },
      { label: 'File descriptors', used: n.fdUsed, limit: n.fdTotal, bytes: false },
      { label: 'Sockets', used: n.socketsUsed, limit: n.socketsTotal, bytes: false },
      { label: 'Erlang processes', used: n.procUsed, limit: n.procTotal, bytes: false },
    ];
    // Metrics that a RabbitMQ version does not report come back as 0 or missing.
    return gauges.filter((g) => !!g.limit);
  }

  percent(g: Gauge): number {
    if (!g.used || !g.limit) return 0;
    return g.inverse ? Math.min(100, (g.limit / g.used) * 100) : Math.min(100, (g.used / g.limit) * 100);
  }

  level(g: Gauge): string {
    const p = this.percent(g);
    return g.alarm || p >= 90 ? 'danger' : p >= 70 ? 'warn' : 'ok';
  }
}
