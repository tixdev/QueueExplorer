import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../../core/api';
import { Auth } from '../../core/auth';
import { Flow, FlowDirection, FlowHop, FlowNode } from '../../core/models';
import { poll } from '../../core/polling';
import { QueueGroups } from '../../core/queue-groups';
import { NumberPipe, RelativePipe } from '../../shared/format';
import { DirectionLabels } from '../../shared/flow-links';
import { matchesSearch, persistedSignal } from '../../shared/list-filters';

type DirectionFilter = '' | FlowDirection;

/** A column of a flow: its nodes, and the hops that leave from them towards later columns. */
interface Stage {
  nodes: FlowNode[];
  hops: (FlowHop & { toNode: FlowNode })[];
}

const PAGE = 40;

/**
 * Migration flows across brokers: RabbitMQ shovels (with the bindings of the exchanges they publish to) and the
 * MSMQ ⇄ RabbitMQ bridge, drawn as pipelines from source to destination with the messages waiting at each step.
 */
@Component({
  selector: 'app-flows',
  imports: [RouterLink, NumberPipe, RelativePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './flows.html',
})
export class FlowsPage {
  private readonly api = inject(Api);
  private readonly auth = inject(Auth);
  private readonly queueGroups = inject(QueueGroups);
  readonly directionLabels = DirectionLabels;
  readonly directions: FlowDirection[] = ['to-rabbitmq', 'to-msmq', 'both', 'unknown', 'rabbitmq'];

  private readonly flowsPoll = poll(() => this.api.flows(), 15000);
  readonly data = this.flowsPoll.value;
  readonly error = this.flowsPoll.error;

  readonly search = persistedSignal('flows.search', '');
  readonly direction = persistedSignal<DirectionFilter>('flows.direction', '');
  readonly bridge = persistedSignal('flows.bridge', '');
  readonly onlyProblems = persistedSignal('flows.onlyProblems', false);
  readonly onlyWithMessages = persistedSignal('flows.onlyWithMessages', false);
  readonly hideErrorPairs = persistedSignal('flows.hideErrorPairs', true);
  /** A flow opened from a queue page (?flow=…) is shown alone. */
  readonly focus = signal<string | null>(null);
  readonly limit = signal(PAGE);

  constructor() {
    const query = inject(ActivatedRoute).snapshot.queryParamMap;
    if (query.has('flow')) this.focus.set(query.get('flow'));
    if (query.has('q')) this.search.set(query.get('q') ?? '');
  }

  /** The selected queue group narrows the flows to those with at least one node in it (on any broker). */
  private readonly inGroup = computed(() => {
    const group = this.queueGroups.active();
    if (!group) return () => true;
    const brokerOf = new Map((this.auth.me()?.clusters ?? []).map((c) => [c.name, c.broker]));
    const matchers = new Map<string, ReturnType<QueueGroups['matcher']>>();
    return (n: FlowNode) => {
      const key = n.broker + '|' + n.cluster;
      let m = matchers.get(key);
      if (!m) matchers.set(key, (m = this.queueGroups.matcher(group, brokerOf.get(n.cluster) ?? n.broker, n.cluster)));
      return m.contains(n.vhost, n.name);
    };
  });

  readonly flows = computed(() => {
    const all = this.data()?.flows ?? [];
    const focus = this.focus();
    if (focus) return all.filter((f) => f.id === focus);
    const search = this.search().trim();
    const direction = this.direction();
    const bridge = this.bridge();
    const inGroup = this.inGroup();
    return all.filter(
      (f) =>
        (!direction || f.direction === direction) &&
        (!bridge || f.bridge === bridge) &&
        (!this.onlyProblems() || f.alerts.length > 0) &&
        (!this.onlyWithMessages() || f.nodes.some((n) => (n.messages ?? 0) > 0)) &&
        (!this.hideErrorPairs() || !isErrorPair(f)) &&
        f.nodes.some(inGroup) &&
        matchesSearch(f.nodes.map((n) => n.name + ' ' + n.vhost).join(' ') + ' ' + f.hops.map((h) => h.name ?? '').join(' '), search),
    );
  });

  readonly shown = computed(() => this.flows().slice(0, this.limit()));

  readonly totals = computed(() => {
    const flows = this.flows();
    return {
      flows: flows.length,
      bridged: flows.filter((f) => f.direction !== 'rabbitmq').length,
      shovels: new Set(flows.flatMap((f) => f.hops.filter((h) => h.kind === 'shovel').map((h) => h.name))).size,
      problems: flows.filter((f) => f.alerts.length > 0).length,
      messages: flows.reduce((n, f) => n + f.nodes.reduce((m, x) => m + (x.messages ?? 0), 0), 0),
    };
  });

  /** Nodes by column, each with the hops leaving it; computed per flow on demand (flows are small). */
  stages(flow: Flow): Stage[] {
    const byId = new Map(flow.nodes.map((n) => [n.id, n]));
    const count = Math.max(...flow.nodes.map((n) => n.stage)) + 1;
    const stages: Stage[] = Array.from({ length: count }, () => ({ nodes: [], hops: [] }));
    for (const n of flow.nodes) stages[n.stage].nodes.push(n);
    for (const h of flow.hops) {
      const from = byId.get(h.from);
      const to = byId.get(h.to);
      if (from && to) stages[from.stage].hops.push({ ...h, toNode: to });
    }
    return stages;
  }

  nodeLink(n: FlowNode): unknown[] | null {
    if (!n.known || !this.auth.me()?.clusters.some((c) => c.name === n.cluster)) return null;
    return n.kind === 'queue' ? ['/c', n.cluster, 'queues', n.vhost, n.name] : ['/c', n.cluster, 'exchanges'];
  }

  /** MSMQ private queues without the "private$\" prefix: the machine is shown next to them. */
  displayName(n: FlowNode): string {
    return n.broker === 'msmq' ? n.name.replace(/^private\$\\/i, '') : n.name || '(default exchange)';
  }

  hopLabel(h: FlowHop): string {
    switch (h.kind) {
      case 'shovel':
        return `shovel${h.state && h.state !== 'running' ? ' · ' + h.state : ''}`;
      case 'binding':
        return h.detail ? `binding · ${h.detail}` : 'binding';
      default:
        return `bridge ${h.name ?? ''}${h.inferred ? ' · ' + (h.detail ?? '') : ''}`;
    }
  }

  hopProblem(h: FlowHop): boolean {
    return h.kind === 'shovel' && h.state !== 'running';
  }

  nodeProblem(n: FlowNode): boolean {
    return !n.known || ((n.messages ?? 0) > 0 && (n.consumers === 0 || /(^|[.\\])error$/i.test(n.name)));
  }

  clearFocus(): void {
    this.focus.set(null);
  }

  more(): void {
    this.limit.update((l) => l + PAGE);
  }
}

/** A bridged pair of NServiceBus error queues and nothing else: present for every endpoint, rarely interesting. */
function isErrorPair(f: Flow): boolean {
  return f.hops.every((h) => h.kind === 'bridge') && f.nodes.every((n) => /(^|[.\\])error$/i.test(n.name));
}
