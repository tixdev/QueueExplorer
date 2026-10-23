import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Queue } from '../../core/models';
import { formatNumber } from '../../shared/format';

/** IBM MQ queue helpers. In the shared Queue shape: messages = current depth, consumers = open input count. */

/** Depth as a fraction of max depth, or null when the queue has no depth (alias, remote, model). */
export function fillRatio(q: Queue): number | null {
  return q.maxDepth && q.messages !== undefined && q.messages !== null ? q.messages / q.maxDepth : null;
}

export function fillLevel(ratio: number | null): 'ok' | 'warn' | 'danger' {
  return ratio === null ? 'ok' : ratio >= 1 ? 'danger' : ratio >= 0.8 ? 'warn' : 'ok';
}

export function isSystemQueue(name: string): boolean {
  return name.startsWith('SYSTEM.');
}

export function isTransmissionQueue(q: Queue): boolean {
  return q.type === 'xmitq';
}

export const QueueTypeLabels: Record<string, string> = {
  local: 'local',
  alias: 'alias',
  remote: 'remote',
  model: 'model',
  xmitq: 'transmission',
  cluster: 'cluster',
};

/** Depth against max depth: a bar plus the percentage as text, so the level never relies on colour. */
@Component({
  selector: 'app-depth-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (ratio(); as r) {
      <span class="depth-meter" [title]="title()">
        <span class="bar"><span [class]="'fill ' + level()" [style.width.%]="percent()"></span></span>
        <span class="depth-meter-text" [class.warn-text]="level() === 'warn'" [class.hot]="level() === 'danger'">{{ label() }}</span>
      </span>
    } @else if (ratio() === 0) {
      <span class="depth-meter" [title]="title()">
        <span class="bar"></span>
        <span class="depth-meter-text muted">0%</span>
      </span>
    } @else {
      <span class="muted">–</span>
    }
  `,
})
export class DepthMeter {
  readonly queue = input.required<Queue>();

  readonly ratio = computed(() => fillRatio(this.queue()));
  readonly level = computed(() => fillLevel(this.ratio()));
  readonly percent = computed(() => Math.min(100, (this.ratio() ?? 0) * 100));
  // Below 1% the queue is not empty: say so rather than rounding to 0.
  readonly label = computed(() => {
    const p = (this.ratio() ?? 0) * 100;
    return p > 0 && p < 1 ? '<1%' : `${Math.round(p)}%`;
  });
  readonly title = computed(() => `${formatNumber(this.queue().messages)} of ${formatNumber(this.queue().maxDepth)} (max depth)`);
}
