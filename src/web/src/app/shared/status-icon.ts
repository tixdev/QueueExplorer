import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type Status = 'good' | 'warning' | 'serious' | 'critical' | 'info';

/** Status is never carried by color alone: each level has its own shape, and callers always add a text label. */
@Component({
  selector: 'app-status-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg width="16" height="16" viewBox="0 0 16 16" [attr.class]="'status-icon ' + status()" aria-hidden="true">
      @switch (status()) {
        @case ('good') {
          <circle cx="8" cy="8" r="7" />
          <path d="M4.8 8.2l2.1 2.1 4.3-4.6" class="status-glyph" />
        }
        @case ('info') {
          <circle cx="8" cy="8" r="7" />
          <path d="M8 7.2v4.2M8 4.6v.2" class="status-glyph" />
        }
        @case ('warning') {
          <path d="M8 1.5l6.8 12H1.2z" />
          <path d="M8 6v3.6M8 11.6v.2" class="status-glyph dark" />
        }
        @default {
          <path d="M5.1 1h5.8L15 5.1v5.8L10.9 15H5.1L1 10.9V5.1z" />
          <path d="M8 4.4v4.4M8 11.2v.2" class="status-glyph" />
        }
      }
    </svg>
  `,
})
export class StatusIcon {
  readonly status = input.required<Status>();
}
