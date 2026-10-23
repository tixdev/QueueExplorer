import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { BrowsedMessage } from '../core/models';
import { BytesPipe, DateTimePipe, JsonValuePipe, RelativePipe, copyToClipboard } from './format';
import { MqMessageDetails } from './mq-message-details';
import { MsmqMessageDetails } from './msmq-message-details';
import { highlight } from './syntax';

type Tab = 'nsb' | 'ibmmq' | 'msmq' | 'body' | 'headers' | 'properties';

/** Detail of a browsed message, with a structured view for NServiceBus messages. */
@Component({
  selector: 'app-message-view',
  imports: [DateTimePipe, RelativePipe, BytesPipe, JsonValuePipe, MqMessageDetails, MsmqMessageDetails],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './message-view.html',
})
export class MessageView {
  readonly message = input.required<BrowsedMessage>();

  private readonly selectedTab = signal<Tab | null>(null);
  readonly mq = computed(() => this.message().ibmMq);
  readonly msmq = computed(() => this.message().msmq);
  /** IBM MQ and MSMQ show their native properties in their own tab instead of the generic Properties table. */
  readonly hasNativeTab = computed(() => !!this.mq() || !!this.msmq());
  readonly tab = computed<Tab>(() => {
    const selected = this.selectedTab();
    // A tab picked for one broker's message may not exist for the next one.
    if (selected === 'nsb' && this.nsb()) return selected;
    if (selected === 'ibmmq' && this.mq()) return selected;
    if (selected === 'msmq' && this.msmq()) return selected;
    if (selected === 'properties' && !this.hasNativeTab()) return selected;
    if (selected === 'body' || selected === 'headers') return selected;
    return this.nsb() ? 'nsb' : this.mq() ? 'ibmmq' : this.msmq() ? 'msmq' : 'body';
  });
  readonly nsb = computed(() => this.message().nServiceBus);
  /** Hex instead of decoded text (IBM MQ bodies, to check a doubtful code page). */
  readonly showHex = signal(false);
  readonly showStack = signal(false);
  readonly copied = signal<string | null>(null);

  readonly failureData = computed(() => Object.entries(this.nsb()?.failure?.data ?? {}));

  readonly headerEntries = computed(() =>
    Object.entries(this.message().headers).sort(([a], [b]) => a.localeCompare(b)),
  );
  readonly propertyEntries = computed(() =>
    Object.entries(this.message().properties).sort(([a], [b]) => a.localeCompare(b)),
  );

  /** Coloured runs of the body when its format was recognised (JSON/XML). */
  readonly bodyTokens = computed(() => {
    const body = this.message().body;
    return highlight(body.text ?? '', body.format);
  });

  readonly hexPreview = computed(() => {
    const base64 = this.message().body.base64 ?? this.message().ibmMq?.rawBodyBase64;
    if (!base64) return '';
    const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0)).slice(0, 512);
    const lines: string[] = [];
    for (let i = 0; i < bytes.length; i += 16) {
      const chunk = Array.from(bytes.slice(i, i + 16));
      const hex = chunk.map((b) => b.toString(16).padStart(2, '0')).join(' ');
      const ascii = chunk.map((b) => (b >= 32 && b < 127 ? String.fromCharCode(b) : '.')).join('');
      lines.push(`${i.toString(16).padStart(6, '0')}  ${hex.padEnd(47)}  ${ascii}`);
    }
    return lines.join('\n');
  });

  readonly kindLabel = computed(() => {
    switch (this.nsb()?.kind) {
      case 'failedMessage':
        return 'Failed message';
      case 'controlMessage':
        return 'Control message';
      case 'subscription':
        return 'Subscription';
      default:
        return 'Message';
    }
  });

  /** Last segment of a type name, keeping nested types readable. */
  shortType(type: string | undefined): string {
    return type?.split('.').pop()?.replace('+', '.') ?? '';
  }

  selectTab(tab: Tab): void {
    this.selectedTab.set(tab);
  }

  async copy(label: string, text: string | undefined): Promise<void> {
    if (!text) return;
    await copyToClipboard(text);
    this.copied.set(label);
    setTimeout(() => this.copied.set(null), 1500);
  }
}
