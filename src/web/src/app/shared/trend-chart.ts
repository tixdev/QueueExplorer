import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal } from '@angular/core';
import { formatNumber } from './format';

export interface TrendPanel {
  label: string;
  values: number[];
}

const PANEL_HEIGHT = 56;
const PANEL_GAP = 18;
/** Room above each panel for its title, clear of the max tick label. */
const TITLE_SPACE = 22;
const AXIS_HEIGHT = 18;
const LEFT = 44;
const RIGHT = 56;
const timeFormat = new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit' });

/**
 * Small multiples sharing one time axis: one single-series area per measure (never a second y-axis), a shared
 * crosshair and one tooltip listing every panel. Gaps in sampling (e.g. API restarts) break the line.
 */
@Component({
  selector: 'app-trend-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'trend-chart' },
  template: `
    @if (layout(); as l) {
      <svg [attr.width]="width()" [attr.height]="l.height" role="img" [attr.aria-label]="ariaLabel()"
           tabindex="0" (pointermove)="hover($event)" (pointerleave)="index.set(null)" (keydown)="key($event)" (blur)="index.set(null)">
        @for (p of l.panels; track p.label) {
          <g [attr.transform]="'translate(0,' + p.top + ')'">
            <text class="viz-panel-title" [attr.x]="0" y="-12">{{ p.label }}</text>
            <line class="viz-grid" [attr.x1]="left" [attr.x2]="width() - right" y1="0" y2="0" />
            <text class="viz-tick" [attr.x]="left - 6" y="4" text-anchor="end">{{ p.maxLabel }}</text>
            <line class="viz-axis" [attr.x1]="left" [attr.x2]="width() - right" [attr.y1]="panelHeight" [attr.y2]="panelHeight" />
            <text class="viz-tick" [attr.x]="left - 6" [attr.y]="panelHeight" text-anchor="end">0</text>
            <path class="viz-area" [attr.d]="p.area" />
            <path class="viz-line" [attr.d]="p.line" />
            @if (p.end; as e) {
              <circle class="viz-dot" [attr.cx]="e.x" [attr.cy]="e.y" r="4" />
              <text class="viz-end-label" [attr.x]="e.x + 8" [attr.y]="e.y + 4">{{ e.label }}</text>
            }
          </g>
        }
        @for (t of l.ticks; track t.x) {
          <text class="viz-tick" [attr.x]="t.x" [attr.y]="l.height - 4" [attr.text-anchor]="t.anchor">{{ t.label }}</text>
        }
        @if (cursor(); as c) {
          <line class="viz-crosshair" [attr.x1]="c.x" [attr.x2]="c.x" [attr.y1]="l.panels[0].top" [attr.y2]="l.plotBottom" />
          @for (d of c.dots; track $index) {
            <circle class="viz-dot" [attr.cx]="c.x" [attr.cy]="d" r="4" />
          }
        }
      </svg>
      @if (cursor(); as c) {
        <div class="viz-tooltip" [style.left.px]="c.tipX" [style.top.px]="0">
          <div class="viz-tooltip-time">{{ c.time }}</div>
          @for (r of c.rows; track r.label) {
            <div class="viz-tooltip-row"><span class="viz-key"></span><strong>{{ r.value }}</strong><span>{{ r.label }}</span></div>
          }
        </div>
      }
    }
  `,
})
export class TrendChart {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly times = input.required<number[]>();
  readonly panels = input.required<TrendPanel[]>();
  /** Samples further apart than this are not joined. */
  readonly maxGapMs = input(3 * 60_000);

  readonly width = signal(600);
  readonly index = signal<number | null>(null);
  readonly left = LEFT;
  readonly right = RIGHT;
  readonly panelHeight = PANEL_HEIGHT;

  readonly ariaLabel = computed(() => this.panels().map((p) => `${p.label}: ${formatNumber(p.values.at(-1) ?? 0)}`).join(', '));

  readonly layout = computed(() => {
    const times = this.times();
    const panels = this.panels();
    if (times.length < 2) return null;
    const width = this.width();
    const t0 = times[0];
    const t1 = times[times.length - 1];
    const x = (t: number) => LEFT + ((t - t0) / (t1 - t0 || 1)) * (width - LEFT - RIGHT);
    const maxGap = this.maxGapMs();

    const laid = panels.map((p, k) => {
      const max = niceMax(Math.max(...p.values, 1));
      const y = (v: number) => PANEL_HEIGHT - (v / max) * PANEL_HEIGHT;
      let line = '';
      let area = '';
      let runStart = -1;
      const closeRun = (end: number) => {
        if (runStart >= 0) area += `L${x(times[end]).toFixed(1)},${PANEL_HEIGHT}L${x(times[runStart]).toFixed(1)},${PANEL_HEIGHT}Z`;
      };
      times.forEach((t, i) => {
        const breaks = i === 0 || t - times[i - 1] > maxGap;
        if (breaks) {
          if (i > 0) closeRun(i - 1);
          runStart = i;
        }
        const cmd = `${breaks ? 'M' : 'L'}${x(t).toFixed(1)},${y(p.values[i]).toFixed(1)}`;
        line += cmd;
        area += cmd;
      });
      closeRun(times.length - 1);
      const last = p.values[p.values.length - 1];
      return {
        label: p.label,
        top: TITLE_SPACE + k * (PANEL_HEIGHT + PANEL_GAP + TITLE_SPACE),
        maxLabel: compact(max),
        line,
        area,
        y,
        end: { x: x(t1), y: y(last), label: compact(last) },
      };
    });
    const plotBottom = laid[laid.length - 1].top + PANEL_HEIGHT;
    const ticks = [
      { x: LEFT, anchor: 'start', label: timeFormat.format(t0) },
      { x: (LEFT + width - RIGHT) / 2, anchor: 'middle', label: timeFormat.format((t0 + t1) / 2) },
      { x: width - RIGHT, anchor: 'end', label: timeFormat.format(t1) },
    ];
    return { panels: laid, ticks, plotBottom, height: plotBottom + AXIS_HEIGHT, x };
  });

  readonly cursor = computed(() => {
    const i = this.index();
    const l = this.layout();
    if (i === null || !l) return null;
    const t = this.times()[i];
    const cx = l.x(t);
    return {
      x: cx,
      tipX: Math.min(Math.max(cx + 12, 0), this.width() - 180),
      time: new Intl.DateTimeFormat('en-GB', { dateStyle: 'short', timeStyle: 'short' }).format(t),
      dots: l.panels.map((p) => p.top + p.y(this.panels()[l.panels.indexOf(p)].values[i])),
      rows: this.panels().map((p) => ({ label: p.label, value: formatNumber(p.values[i]) })),
    };
  });

  constructor() {
    const observer = new ResizeObserver((entries) => this.width.set(Math.max(280, Math.floor(entries[0].contentRect.width))));
    effect((onCleanup) => {
      observer.observe(this.host.nativeElement);
      onCleanup(() => observer.disconnect());
    });
  }

  /** The crosshair snaps to the nearest sample: readers aim at a time, not at a 2px line. */
  hover(event: PointerEvent): void {
    const l = this.layout();
    if (!l) return;
    const rect = (event.currentTarget as SVGElement).getBoundingClientRect();
    const px = event.clientX - rect.left;
    const times = this.times();
    let best = 0;
    for (let i = 1; i < times.length; i++) if (Math.abs(l.x(times[i]) - px) < Math.abs(l.x(times[best]) - px)) best = i;
    this.index.set(best);
  }

  key(event: KeyboardEvent): void {
    const n = this.times().length;
    const i = this.index() ?? n - 1;
    if (event.key === 'ArrowLeft') this.index.set(Math.max(0, i - 1));
    else if (event.key === 'ArrowRight') this.index.set(Math.min(n - 1, i + 1));
    else if (event.key === 'Escape') this.index.set(null);
    else return;
    event.preventDefault();
  }
}

function niceMax(v: number): number {
  const exp = Math.pow(10, Math.floor(Math.log10(v)));
  const n = v / exp;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * exp;
}

function compact(v: number): string {
  return new Intl.NumberFormat('en-GB', { notation: 'compact', maximumFractionDigits: 1 }).format(v);
}
