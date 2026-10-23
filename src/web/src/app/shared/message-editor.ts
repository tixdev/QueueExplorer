import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import { Api } from '../core/api';
import { errorMessage } from '../core/http';
import { BrowsedMessage, JsonValue, PropertiesEdit } from '../core/models';

interface HeaderRow {
  key: string;
  value: string | null;
  /** Untouched existing header: sent as "keep" so its AMQP type is preserved. */
  keep: boolean;
  /** Arrays and tables cannot be edited as text. */
  complex: boolean;
}

const PROPERTY_FIELDS: { key: keyof PropertiesEdit; source: string; label: string; numeric?: boolean }[] = [
  { key: 'contentType', source: 'content_type', label: 'Content type' },
  { key: 'contentEncoding', source: 'content_encoding', label: 'Content encoding' },
  { key: 'messageId', source: 'message_id', label: 'Message id' },
  { key: 'correlationId', source: 'correlation_id', label: 'Correlation id' },
  { key: 'type', source: 'type', label: 'Type' },
  { key: 'replyTo', source: 'reply_to', label: 'Reply to' },
  { key: 'appId', source: 'app_id', label: 'App id' },
  { key: 'expiration', source: 'expiration', label: 'Expiration (ms)' },
  { key: 'deliveryMode', source: 'delivery_mode', label: 'Delivery mode (1/2)', numeric: true },
  { key: 'priority', source: 'priority', label: 'Priority', numeric: true },
];

/** Edits a staged message: body, AMQP properties and headers. */
@Component({
  selector: 'app-message-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="editor">
      <div class="body-toolbar">
        <strong>Edit message</strong>
        <span class="spacer"></span>
        @if (message().editedAt) {
          <button class="small" (click)="revert()" [disabled]="busy()" title="Restore the message as it was staged">Revert to original</button>
        }
        <button class="small" (click)="cancel.emit()" [disabled]="busy()">Cancel</button>
        <button class="small primary" (click)="save()" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save' }}</button>
      </div>
      @if (error(); as e) { <div class="banner error">{{ e }}</div> }

      <h4>Body</h4>
      @if (bodyEditable()) {
        <div class="body-toolbar">
          <button class="small" (click)="formatJson()">Format JSON</button>
          @if (jsonError(); as je) { <span class="warn-text small">Not valid JSON: {{ je }}</span> }
        </div>
        <textarea class="code-input" rows="14" [value]="body()" (input)="body.set($any($event.target).value)"></textarea>
      } @else {
        <p class="muted">Binary body: kept as it is.</p>
      }

      <h4>Properties</h4>
      <div class="form-grid">
        @for (f of fields; track f.key) {
          <label>
            <span>{{ f.label }}</span>
            <input [type]="f.numeric ? 'number' : 'text'" [value]="properties()[f.key] ?? ''" (input)="setProperty(f.key, $any($event.target).value, f.numeric)" />
          </label>
        }
      </div>

      <h4>Headers <span class="count">{{ headers().length }}</span></h4>
      <table class="kv edit-headers">
        @for (h of headers(); track $index; let i = $index) {
          <tr>
            <th><input [value]="h.key" [readonly]="h.keep || h.complex" (input)="update(i, { key: $any($event.target).value })" /></th>
            <td>
              <input [value]="h.value ?? ''" [readonly]="h.complex" [title]="h.complex ? 'Arrays and tables cannot be edited' : ''"
                (input)="update(i, { value: $any($event.target).value, keep: false })" />
            </td>
            <td class="row-action"><button class="small" (click)="remove(i)" title="Remove header">✕</button></td>
          </tr>
        }
      </table>
      <button class="small" (click)="add()">Add header</button>
    </div>
  `,
})
export class MessageEditor {
  private readonly api = inject(Api);

  readonly message = input.required<BrowsedMessage>();
  readonly batchId = input.required<number>();
  readonly saved = output<BrowsedMessage>();
  readonly cancel = output<void>();

  readonly fields = PROPERTY_FIELDS;
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly bodyEditable = computed(() => this.message().body.format !== 'binary');
  readonly body = linkedSignal(() => this.message().body.text ?? '');
  readonly properties = linkedSignal<PropertiesEdit>(() => {
    const p = this.message().properties;
    return Object.fromEntries(PROPERTY_FIELDS.map((f) => [f.key, (p[f.source] as string | number | undefined) ?? null])) as PropertiesEdit;
  });
  readonly headers = linkedSignal<HeaderRow[]>(() =>
    Object.entries(this.message().headers)
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([key, value]) => ({ key, value: asText(value), keep: true, complex: value !== null && typeof value === 'object' })),
  );

  readonly jsonError = computed(() => {
    const text = this.body();
    if (!this.isJson() || !text.trim()) return null;
    try {
      JSON.parse(text);
      return null;
    } catch (e) {
      return (e as Error).message;
    }
  });

  private isJson(): boolean {
    return (this.properties().contentType ?? this.message().contentType ?? '').includes('json') || this.message().body.format === 'json';
  }

  setProperty(key: keyof PropertiesEdit, value: string, numeric?: boolean): void {
    const parsed = value === '' ? null : numeric ? Number(value) : value;
    this.properties.update((p) => ({ ...p, [key]: parsed }));
  }

  update(index: number, change: Partial<HeaderRow>): void {
    this.headers.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...change } : r)));
  }

  add(): void {
    this.headers.update((rows) => [...rows, { key: '', value: '', keep: false, complex: false }]);
  }

  remove(index: number): void {
    this.headers.update((rows) => rows.filter((_, i) => i !== index));
  }

  formatJson(): void {
    try {
      this.body.set(JSON.stringify(JSON.parse(this.body()), null, 2));
    } catch {
      // jsonError already explains why.
    }
  }

  save(): void {
    const m = this.message();
    this.busy.set(true);
    this.error.set(null);
    this.api
      .editStaged(this.batchId(), m.stagedId!, {
        properties: this.properties(),
        headers: this.headers()
          .filter((h) => h.key.trim())
          .map((h) => (h.keep || h.complex ? { key: h.key, keep: true } : { key: h.key.trim(), value: h.value })),
        body: this.bodyEditable() && this.body() !== (m.body.text ?? '') ? this.body() : null,
      })
      .subscribe({
        next: (updated) => {
          this.busy.set(false);
          this.saved.emit(updated);
        },
        error: (e) => {
          this.busy.set(false);
          this.error.set(errorMessage(e));
        },
      });
  }

  revert(): void {
    this.busy.set(true);
    this.api.revertStaged(this.batchId(), this.message().stagedId!).subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.saved.emit(updated);
      },
      error: (e) => {
        this.busy.set(false);
        this.error.set(errorMessage(e));
      },
    });
  }
}

function asText(value: JsonValue): string | null {
  if (value === null || value === undefined) return null;
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}
