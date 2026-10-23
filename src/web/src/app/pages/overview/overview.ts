import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { EMPTY, map } from 'rxjs';
import { Api } from '../../core/api';
import { Auth } from '../../core/auth';
import { ClusterStore } from '../../core/cluster-store';
import { errorMessage } from '../../core/http';
import { Actions, ErrorQueueSummary, FailureGroup, Queue, RmqNode } from '../../core/models';
import { poll } from '../../core/polling';
import { BytesPipe, NumberPipe, RelativePipe, UptimePipe, isNsbInfrastructure } from '../../shared/format';
import { persistedSignal } from '../../shared/list-filters';
import { Sparkline } from '../../shared/sparkline';
import { Status, StatusIcon } from '../../shared/status-icon';
import { TrendChart, TrendPanel } from '../../shared/trend-chart';

interface HealthItem {
  status: Status;
  text: string;
  detail?: string;
  link?: unknown[];
  query?: Record<string, string | number>;
}

interface Gauge {
  label: string;
  used?: number;
  limit?: number;
  alarm?: boolean;
  bytes: boolean;
  /** For disk the limit is a floor, not a ceiling. */
  inverse?: boolean;
}

const TREND_WINDOWS = [
  { minutes: 60, label: '1h' },
  { minutes: 360, label: '6h' },
  { minutes: 1440, label: '24h' },
];

@Component({
  selector: 'app-overview',
  imports: [NumberPipe, BytesPipe, UptimePipe, RelativePipe, RouterLink, Sparkline, StatusIcon, TrendChart],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overview.html',
})
export class OverviewPage {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  readonly store = inject(ClusterStore);

  readonly windows = TREND_WINDOWS;
  readonly window = persistedSignal('overview.trendWindow', 60);
  readonly trendTable = signal(false);

  private can(action: string): boolean {
    return this.auth.can(this.store.cluster() ?? '', action);
  }

  // Everything below polls endpoints served from the server's snapshot, so none of it reaches RabbitMQ.
  private readonly overviewPoll = poll(() => {
    const c = this.store.cluster();
    return c ? this.api.overview(c) : EMPTY;
  }, 5000);
  private readonly shovelsPoll = poll(() => {
    const c = this.store.cluster();
    return c && this.can(Actions.shovelsRead) ? this.api.shovels(c) : EMPTY;
  }, 15000);
  private readonly trendPoll = poll(() => {
    const c = this.store.cluster();
    const minutes = this.window();
    return c && this.can(Actions.overviewRead)
      ? this.api.trend(c, minutes).pipe(map((t) => ({ etag: null, body: { minutes, ...t } })))
      : EMPTY;
  }, 30000);
  private readonly errorsPoll = poll(() => {
    const c = this.store.cluster();
    return c && this.can(Actions.queuesRead) ? this.api.errorQueues(c).pipe(map((e) => ({ etag: null, body: e }))) : EMPTY;
  }, 15000);

  readonly data = this.overviewPoll.value;
  readonly error = this.overviewPoll.error;
  readonly overview = computed(() => this.data()?.overview);
  readonly nodes = computed(() => this.data()?.nodes);
  readonly queues = computed(() => this.store.topology()?.queues ?? []);

  // ---- KPIs -------------------------------------------------------------------------------------------------------
  readonly trend = computed(() => {
    const t = this.trendPoll.value();
    return t && t.minutes === this.window() ? t : undefined;
  });
  readonly trendTimes = computed(() => (this.trend()?.samples ?? []).map((s) => new Date(s.at).getTime()));
  readonly trendPanels = computed<TrendPanel[]>(() => {
    const samples = this.trend()?.samples ?? [];
    return [
      { label: 'All messages', values: samples.map((s) => s.messages) },
      { label: 'In error queues', values: samples.map((s) => s.errorMessages) },
    ];
  });
  readonly trendTail = computed(() => (this.trend()?.samples ?? []).slice(-12).reverse());
  readonly sparkMessages = computed(() => (this.trend()?.samples ?? []).slice(-30).map((s) => s.messages));
  readonly sparkErrors = computed(() => (this.trend()?.samples ?? []).slice(-30).map((s) => s.errorMessages));

  readonly errorMessages = computed(() => this.queues().filter((q) => isError(q.name)).reduce((n, q) => n + (q.messages ?? 0), 0));

  // ---- Stopped endpoints: messages waiting, nothing consuming them ------------------------------------------------
  readonly stopped = computed(() =>
    this.queues()
      .filter((q) => (q.messages ?? 0) > 0 && (q.consumers ?? 0) === 0 && !isError(q.name) && !isAudit(q.name) && !isNsbInfrastructure(q.name))
      .sort(byMessages),
  );

  readonly topQueues = computed(() =>
    this.queues()
      .filter((q) => (q.messages ?? 0) > 0 && !isNsbInfrastructure(q.name))
      .sort(byMessages)
      .slice(0, 8),
  );
  readonly topMax = computed(() => this.topQueues()[0]?.messages ?? 1);

  // ---- Per vhost ------------------------------------------------------------------------------------------------
  readonly vhosts = computed(() => {
    const rows = new Map<string, { vhost: string; queues: number; messages: number; errors: number; stopped: number }>();
    const stopped = new Set(this.stopped());
    for (const q of this.queues()) {
      if (isNsbInfrastructure(q.name)) continue;
      const r = rows.get(q.vhost) ?? { vhost: q.vhost, queues: 0, messages: 0, errors: 0, stopped: 0 };
      r.queues++;
      r.messages += q.messages ?? 0;
      if (isError(q.name)) r.errors += q.messages ?? 0;
      if (stopped.has(q)) r.stopped++;
      rows.set(q.vhost, r);
    }
    return [...rows.values()].sort((a, b) => b.messages - a.messages || a.vhost.localeCompare(b.vhost));
  });
  readonly vhostMax = computed(() => Math.max(1, ...this.vhosts().map((v) => v.messages)));

  // ---- Failures (NServiceBus error queues) -----------------------------------------------------------------------
  readonly errorQueues = computed<ErrorQueueSummary[]>(() => this.errorsPoll.value() ?? []);
  readonly errorQueuesWithMessages = computed(() => this.errorQueues().filter((q) => q.messages > 0));
  readonly canAnalyze = computed(() => this.errorQueuesWithMessages().some((q) => q.canAnalyze));
  readonly analyzing = signal(false);
  readonly analyzeError = signal<string | null>(null);
  readonly failureRows = computed(() =>
    this.errorQueues()
      .flatMap((q) => (q.analysis?.groups ?? []).map((g) => ({ ...g, vhost: q.vhost, queue: q.queue })))
      .sort((a, b) => b.count - a.count)
      .slice(0, 12),
  );
  readonly failureMax = computed(() => this.failureRows()[0]?.count ?? 1);
  readonly lastAnalysis = computed(() =>
    this.errorQueues()
      .map((q) => q.analysis)
      .filter((a) => !!a)
      .sort((a, b) => b!.analyzedAt.localeCompare(a!.analyzedAt))[0],
  );
  readonly analysisCoverage = computed(() => {
    const analysed = this.errorQueues().filter((q) => q.analysis && !q.analysis.skipped);
    return {
      read: analysed.reduce((n, q) => n + (q.analysis?.analyzed ?? 0), 0),
      changed: analysed.filter((q) => q.analysis!.depthWhenAnalyzed !== q.messages).length,
      skipped: this.errorQueues().filter((q) => q.analysis?.skipped),
    };
  });

  // ---- Health ---------------------------------------------------------------------------------------------------
  readonly health = computed<HealthItem[]>(() => {
    const items: HealthItem[] = [];
    const cluster = this.store.cluster();
    const snapshot = this.store.topology()?.snapshot ?? this.data()?.snapshot;
    if (snapshot?.lastError) items.push({ status: 'critical', text: 'Cannot refresh from RabbitMQ', detail: snapshot.lastError });

    for (const n of this.nodes() ?? []) {
      if (!n.running) items.push({ status: 'critical', text: `Node ${n.name} is down` });
      if (n.memAlarm) items.push({ status: 'critical', text: `Memory alarm on ${n.name}`, detail: 'Publishers are blocked' });
      if (n.diskFreeAlarm) items.push({ status: 'critical', text: `Disk alarm on ${n.name}`, detail: 'Publishers are blocked' });
      const mem = ratio(n.memUsed, n.memLimit);
      if (!n.memAlarm && mem >= 0.8) items.push({ status: 'warning', text: `Memory at ${Math.round(mem * 100)}% on ${n.name}` });
      const fd = ratio(n.fdUsed, n.fdTotal);
      if (fd >= 0.8) items.push({ status: 'warning', text: `File descriptors at ${Math.round(fd * 100)}% on ${n.name}` });
    }

    const notRunning = this.queues().filter((q) => q.state && q.state !== 'running');
    if (notRunning.length)
      items.push({ status: 'serious', text: `${notRunning.length} queue${notRunning.length > 1 ? 's' : ''} not running`, detail: notRunning.slice(0, 3).map((q) => `${q.vhost}/${q.name} (${q.state})`).join(', ') });

    const shovels = (this.shovelsPoll.value() ?? []).filter((s) => s.state !== 'running');
    if (shovels.length)
      items.push({ status: 'serious', text: `${shovels.length} shovel${shovels.length > 1 ? 's' : ''} not running`, detail: shovels.slice(0, 3).map((s) => s.name).join(', '), link: ['/c', cluster, 'shovels'] });

    const stopped = this.stopped().length;
    if (stopped)
      items.push({ status: 'warning', text: `${stopped} endpoint${stopped > 1 ? 's' : ''} with messages and no consumers`, link: ['/c', cluster, 'queues'], query: { withMessages: 1, sort: 'messages' } });

    const errors = this.errorMessages();
    if (errors) items.push({ status: 'warning', text: `${errors.toLocaleString('en-GB')} messages in error queues`, link: ['/c', cluster, 'queues'], query: { withMessages: 1, sort: 'messages' } });
    return items;
  });
  readonly worst = computed<Status>(() => {
    const order: Status[] = ['critical', 'serious', 'warning'];
    return order.find((s) => this.health().some((h) => h.status === s)) ?? 'good';
  });

  setWindow(minutes: number): void {
    this.window.set(minutes);
    this.trendPoll.reload();
  }

  analyze(): void {
    const c = this.store.cluster();
    if (!c) return;
    this.analyzing.set(true);
    this.analyzeError.set(null);
    this.api.analyzeErrors(c).subscribe({
      next: () => {
        this.analyzing.set(false);
        this.errorsPoll.reload();
      },
      error: (e) => {
        this.analyzing.set(false);
        this.analyzeError.set(errorMessage(e));
      },
    });
  }

  shortType(type: string): string {
    return type.startsWith('(') ? type : (type.split('.').pop() ?? type);
  }

  failureKey(f: FailureGroup & { vhost: string; queue: string }): string {
    return `${f.vhost}/${f.queue}/${f.endpoint}/${f.exceptionType}`;
  }

  gauges(n: RmqNode): Gauge[] {
    const gauges: Gauge[] = [
      { label: 'Memory', used: n.memUsed, limit: n.memLimit, alarm: n.memAlarm, bytes: true },
      { label: 'Free disk', used: n.diskFree, limit: n.diskFreeLimit, alarm: n.diskFreeAlarm, bytes: true, inverse: true },
      { label: 'File descriptors', used: n.fdUsed, limit: n.fdTotal, bytes: false },
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

  isError = isError;
}

function isError(name: string): boolean {
  return name === 'error' || name.endsWith('.error');
}

function isAudit(name: string): boolean {
  return name === 'audit' || name.endsWith('.audit');
}

function byMessages(a: Queue, b: Queue): number {
  return (b.messages ?? 0) - (a.messages ?? 0) || a.name.localeCompare(b.name);
}

function ratio(used?: number, limit?: number): number {
  return used && limit ? used / limit : 0;
}
