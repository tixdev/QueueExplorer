import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { MsmqMessageView } from '../core/models';
import { copyToClipboard, formatDateTime } from './format';

/** MSMQ part of a message: why it was dead-lettered (if it was), its native properties and the extension. */
@Component({
  selector: 'app-msmq-message-details',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let v = view();
    <section class="mq-details">
      @if (deadLettered()) {
        <div class="banner warn small">Dead-lettered: {{ v.classDescription }}</div>
      }
      <h4>Message properties</h4>
      <table class="kv">
        <tr>
          <th>Message id</th>
          <td class="mono copyable" (click)="copy(v.id)">
            {{ v.id }} @if (copied()) { <span class="ok">copied</span> }
          </td>
        </tr>
        @for (p of properties(); track p.name) {
          <tr><th>{{ p.name }}</th><td class="mono" [title]="p.type === 'datetime' ? (p.value ?? '') : ''">{{ display(p.value, p.type) }}</td></tr>
        }
      </table>

      <h4>Extension</h4>
      @switch (v.extension) {
        @case ('NServiceBus headers') {
          <p class="muted small">NServiceBus headers, shown in the Header and NServiceBus tabs.</p>
        }
        @case ('text') {
          <pre class="code">{{ v.extensionText }}</pre>
        }
        @case ('binary') {
          <p class="muted small">Binary data (first bytes, hex):</p>
          <pre class="code">{{ v.extensionText }}</pre>
        }
        @default {
          <p class="muted small">Empty.</p>
        }
      }
    </section>
  `,
})
export class MsmqMessageDetails {
  readonly view = input.required<MsmqMessageView>();
  readonly copied = signal(false);

  /** Ids are shown on their own row (copyable); the rest in the server's order. */
  readonly properties = computed(() => this.view().properties.filter((p) => p.name !== 'Message id'));
  /** Negative acknowledgements (class 0x8000 and above) are the reasons MSMQ dead-letters a message. */
  readonly deadLettered = computed(() => (this.view().class ?? 0) >= 0x8000);

  display(value: string | undefined, type: string | undefined): string {
    if (!value) return '–';
    return type === 'datetime' ? formatDateTime(value) : value;
  }

  async copy(text: string): Promise<void> {
    await copyToClipboard(text);
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 1500);
  }
}
