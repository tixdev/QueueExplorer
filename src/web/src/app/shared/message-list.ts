import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { BrowsedMessage, MqMessageView } from '../core/models';
import { BytesPipe, DateTimePipe, RelativePipe } from './format';

/** Message list with multi-selection (by message key), shared by the queue and staging pages. */
@Component({
  selector: 'app-message-list',
  imports: [BytesPipe, DateTimePipe, RelativePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="message-list-wrap">
      <label class="select-all check">
        <input type="checkbox" [checked]="allSelected()" [indeterminate]="someSelected()" (change)="toggleAll()" />
        {{ selectedKeys().size ? selectedKeys().size + ' selected' : 'Select all' }}
      </label>
      <ul class="message-list">
        @for (m of messages(); track m.stagedId ?? m.index) {
          <li [class.selected]="current() === m" [class.failed]="m.nServiceBus?.kind === 'failedMessage'" [class.discarded]="m.discarded" (click)="current.set(m)">
            <input type="checkbox" class="pick" [checked]="selectedKeys().has(id(m))" (click)="$event.stopPropagation()" (change)="toggle(m)" />
            <div class="message-body">
              <div class="message-title">
                <span>{{ title(m) }}</span>
                @if (m.sentAt; as t) { <time class="muted nowrap" [attr.datetime]="t" [title]="t | relative">{{ t | datetime }}</time> }
              </div>
              <div class="message-subtitle">{{ subtitle(m) }}</div>
              <div class="message-meta">
                <span>#{{ m.index + 1 }}</span>
                <span>{{ m.payloadBytes | bytes }}</span>
                @if (m.ibmMq; as mq) {
                  <span class="badge muted" title="MQ format">{{ mq.headers[0]?.format?.trim() ?? mq.bodyFormat }}</span>
                } @else if (!m.nServiceBus) {
                  <span class="badge muted">not NSB</span>
                }
                @if (m.discarded) { <span class="badge muted">discarded</span> }
                @if (m.editedAt) { <span class="badge warn" [title]="'edited by ' + m.editedBy">edited</span> }
                @if (m.lastPublishedTo) { <span class="badge ok" [title]="m.lastPublishedTo">published</span> }
              </div>
            </div>
          </li>
        } @empty {
          <li class="muted">No messages</li>
        }
      </ul>
    </div>
  `,
})
export class MessageList {
  readonly messages = input.required<BrowsedMessage[]>();
  /** Selected ids: the message key, or the staged id for staging messages. */
  readonly selectedKeys = model<ReadonlySet<string>>(new Set());
  readonly current = model<BrowsedMessage | null>(null);

  readonly allSelected = computed(() => this.messages().length > 0 && this.messages().every((m) => this.selectedKeys().has(this.id(m))));
  readonly someSelected = computed(() => this.selectedKeys().size > 0 && !this.allSelected());

  id(m: BrowsedMessage): string {
    return m.stagedId !== undefined && m.stagedId !== null ? String(m.stagedId) : m.key;
  }

  toggle(m: BrowsedMessage): void {
    const next = new Set(this.selectedKeys());
    const id = this.id(m);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.selectedKeys.set(next);
  }

  toggleAll(): void {
    this.selectedKeys.set(this.allSelected() ? new Set() : new Set(this.messages().map((m) => this.id(m))));
  }

  title(m: BrowsedMessage): string {
    return m.nServiceBus?.messageTypes[0]?.name ?? (m.ibmMq ? mqTitle(m.ibmMq) : undefined) ?? (m.body.format === 'binary' ? 'Binary' : (m.contentType ?? 'Message'));
  }

  subtitle(m: BrowsedMessage): string {
    const n = m.nServiceBus;
    if (n?.failure) return `${n.failure.exceptionType?.split('.').pop() ?? ''}: ${n.failure.message ?? ''}`;
    if (n) return n.originatingEndpoint ?? '';
    return m.body.text?.slice(0, 80) ?? '';
  }
}

/** Dead letters by their reason (e.g. MQRC_Q_FULL), PCF messages by their command; otherwise the generic title. */
function mqTitle(v: MqMessageView): string | undefined {
  const reason = v.headers.find((h) => h.format.trim() === 'MQDEAD')?.fields.find((f) => f.name === 'Reason')?.value;
  if (reason) return 'Dead letter · ' + (reason.split(' ')[1] ?? reason);
  return v.pcf?.commandName;
}
