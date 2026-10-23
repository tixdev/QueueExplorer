import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

const W = 96;
const H = 26;

/** Stat-tile sparkline: history in the de-emphasis hue, the current value as an accent dot with a surface ring. */
@Component({
  selector: 'app-sparkline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (path(); as p) {
      <svg class="sparkline" [attr.width]="w" [attr.height]="h" [attr.viewBox]="'0 0 ' + w + ' ' + h" aria-hidden="true">
        <path [attr.d]="p.line" class="spark-line" />
        <circle [attr.cx]="p.endX" [attr.cy]="p.endY" r="3" class="spark-end" />
      </svg>
    }
  `,
})
export class Sparkline {
  readonly values = input.required<number[]>();
  readonly w = W;
  readonly h = H;

  readonly path = computed(() => {
    const v = this.values();
    if (v.length < 2) return null;
    const max = Math.max(...v);
    const min = Math.min(...v);
    const span = max - min || 1;
    const x = (i: number) => 3 + (i / (v.length - 1)) * (W - 6);
    const y = (n: number) => H - 4 - ((n - min) / span) * (H - 8);
    const line = v.map((n, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(n).toFixed(1)}`).join('');
    return { line, endX: x(v.length - 1), endY: y(v[v.length - 1]) };
  });
}
