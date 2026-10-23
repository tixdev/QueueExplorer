import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Api } from '../../core/api';
import { errorMessage } from '../../core/http';
import { AuditEntry } from '../../core/models';
import { DateTimePipe } from '../../shared/format';

@Component({
  selector: 'app-audit',
  imports: [DateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './audit.html',
})
export class AuditPage {
  private readonly api = inject(Api);

  readonly entries = signal<AuditEntry[]>([]);
  readonly loading = signal(false);
  readonly done = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  load(): void {
    const last = this.entries().at(-1);
    this.loading.set(true);
    this.api.audit(last?.id).subscribe({
      next: (page) => {
        this.entries.update((e) => [...e, ...page]);
        this.done.set(page.length < 100);
        this.loading.set(false);
      },
      error: (e) => {
        this.error.set(errorMessage(e));
        this.loading.set(false);
      },
    });
  }

  reload(): void {
    this.entries.set([]);
    this.done.set(false);
    this.load();
  }
}
