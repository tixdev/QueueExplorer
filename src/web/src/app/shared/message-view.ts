import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { BrowsedMessage } from '../core/models';
import { BytesPipe, DateTimePipe, JsonValuePipe, RelativePipe, copyToClipboard } from './format';

type Tab = 'nsb' | 'body' | 'headers' | 'properties';

/** Detail of a browsed message, with a structured view for NServiceBus messages. */
@Component({
  selector: 'app-message-view',
  imports: [DateTimePipe, RelativePipe, BytesPipe, JsonValuePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './message-view.html',
})
export class MessageView {
  readonly message = input.required<BrowsedMessage>();

  private readonly selectedTab = signal<Tab | null>(null);
  readonly tab = computed<Tab>(() => this.selectedTab() ?? (this.message().nServiceBus ? 'nsb' : 'body'));
  readonly nsb = computed(() => this.message().nServiceBus);
  readonly showStack = signal(false);
  readonly copied = signal<string | null>(null);

  readonly failureData = computed(() => Object.entries(this.nsb()?.failure?.data ?? {}));

  readonly headerEntries = computed(() =>
    Object.entries(this.message().headers).sort(([a], [b]) => a.localeCompare(b)),
  );
  readonly propertyEntries = computed(() =>
    Object.entries(this.message().properties).sort(([a], [b]) => a.localeCompare(b)),
  );

  readonly hexPreview = computed(() => {
    const base64 = this.message().body.base64;
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
