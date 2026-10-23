import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, signal } from '@angular/core';
import { MqMessageView } from '../core/models';
import { copyToClipboard, formatDateTime } from './format';

const ISO_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/;

/** Fields of the MQMD shown first; the rest follow in the server's order. */
const MQMD_FIRST = ['Format', 'CCSID', 'Encoding', 'Message type', 'Put at', 'Put by', 'Reply to', 'Backout count'];

/** IBM MQ part of a message: header chain, MQMD, ids, dead-letter / transmission headers, RFH2 folders, PCF. */
@Component({
  selector: 'app-mq-message-details',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let v = view();
    <section class="mq-details">
      <div class="mq-chain" aria-label="Header chain">
        @for (h of v.headers; track $index) {
          <button class="chip mono" (click)="scrollTo($index)" [title]="h.title">{{ h.format.trim() }}</button>
          <span class="muted">→</span>
        }
        <span class="chip mono static" title="Format of the body">{{ v.bodyFormat }}</span>
      </div>
      @if (v.warning) {
        <div class="banner warn small">{{ v.warning }}</div>
      }

      <h4>Message descriptor (MQMD)</h4>
      <table class="kv">
        <tr>
          <th>MsgId</th>
          <td class="mono copyable" (click)="copy('msgid', v.messageIdHex)">
            {{ v.messageIdHex }} @if (v.messageIdText) { <span class="muted">“{{ v.messageIdText }}”</span> }
            @if (copied() === 'msgid') { <span class="ok">copied</span> }
          </td>
        </tr>
        <tr>
          <th>CorrelId</th>
          <td class="mono copyable" (click)="copy('correlid', v.correlationIdHex)">
            {{ v.correlationIdHex }} @if (v.correlationIdText) { <span class="muted">“{{ v.correlationIdText }}”</span> }
            @if (copied() === 'correlid') { <span class="ok">copied</span> }
          </td>
        </tr>
        @for (p of mqmd(); track p.name) {
          <tr><th>{{ p.name }}</th><td class="mono" [title]="isTime(p.value) ? p.value : ''">{{ display(p.value) }}</td></tr>
        }
      </table>

      @for (h of v.headers; track $index) {
        <div class="mq-header" [attr.data-header]="$index">
          <h4>{{ h.title }} <span class="muted mono small">{{ h.format.trim() }}</span></h4>
          @if (h.fields.length) {
            <table class="kv">
              @for (f of h.fields; track f.name) {
                <tr><th>{{ f.name }}</th><td class="mono" [title]="isTime(f.value) ? f.value : ''">{{ display(f.value) }}</td></tr>
              }
            </table>
          }
          @for (folder of h.folders; track folder.name) {
            <details class="header-group" [open]="folder.name !== 'mcd'">
              <summary class="mono">{{ folder.name }} <span class="count">{{ folder.properties.length }}</span></summary>
              @if (folder.properties.length) {
                <table class="kv">
                  @for (p of folder.properties; track p.name) {
                    <tr><th class="mono">{{ p.name }}</th><td class="mono">{{ p.value }} @if (p.type) { <span class="muted small">{{ p.type }}</span> }</td></tr>
                  }
                </table>
              } @else {
                <pre class="code">{{ folder.xml }}</pre>
              }
            </details>
          }
        </div>
      }

      @if (v.pcf; as pcf) {
        <h4>PCF {{ pcf.commandName }}</h4>
        <p class="muted small">
          {{ pcf.last ? 'last message of the response' : 'more messages follow' }}
          @if (pcf.reason) { · <span class="hot">{{ pcf.reason }} {{ pcf.reasonName }}</span> }
        </p>
        <table class="kv">
          @for (p of pcf.parameters; track $index) {
            <tr><th class="mono">{{ p.name }}</th><td class="mono">{{ p.value }}</td></tr>
          }
        </table>
      }
    </section>
  `,
})
export class MqMessageDetails {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly view = input.required<MqMessageView>();
  readonly copied = signal<string | null>(null);

  readonly mqmd = computed(() => {
    const fields = this.view().mqmd;
    const rank = (name: string) => {
      const i = MQMD_FIRST.indexOf(name);
      return i < 0 ? MQMD_FIRST.length : i;
    };
    return [...fields].sort((a, b) => rank(a.name) - rank(b.name));
  });

  isTime(value: string | undefined): boolean {
    return !!value && ISO_TIME.test(value);
  }

  /** Times in the viewer's local time zone (the exact UTC value stays in the tooltip). */
  display(value: string | undefined): string {
    return value && ISO_TIME.test(value) ? formatDateTime(value) : (value ?? '');
  }

  scrollTo(index: number): void {
    this.host.nativeElement.querySelector(`[data-header="${index}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  async copy(label: string, text: string): Promise<void> {
    await copyToClipboard(text);
    this.copied.set(label);
    setTimeout(() => this.copied.set(null), 1500);
  }
}
