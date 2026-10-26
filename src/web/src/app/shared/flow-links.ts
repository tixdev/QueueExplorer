import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { Api } from '../core/api';
import { BrokerId, Flow, FlowDirection } from '../core/models';

export const DirectionLabels: Record<FlowDirection, string> = {
  'to-msmq': 'RabbitMQ → MSMQ',
  'to-rabbitmq': 'MSMQ → RabbitMQ',
  both: 'both directions',
  unknown: 'bridged, direction unknown',
  rabbitmq: 'RabbitMQ only',
};

/**
 * "Part of a migration flow" for a queue or exchange: the flows that go through it (shovels, MSMQ ⇄ RabbitMQ bridge),
 * each linking to the Flows page. Nothing is shown when it is in none.
 */
@Component({
  selector: 'app-flow-links',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (flows().length) {
      <p class="flow-links-inline small">
        <span class="muted">Migration flow{{ flows().length > 1 ? 's' : '' }}:</span>
        @for (f of flows().slice(0, 5); track f.id) {
          <a [routerLink]="['/c', cluster(), 'flows']" [queryParams]="{ flow: f.id }">
            <span class="badge flow-direction" [attr.data-direction]="f.direction">{{ labels[f.direction] }}</span>
            <span class="mono">{{ f.title }}</span>
          </a>
          @if (f.alerts.length) { <span class="warn-text">({{ f.alerts.length }} problem{{ f.alerts.length > 1 ? 's' : '' }})</span> }
        }
        @if (flows().length > 5) { <span class="muted">and {{ flows().length - 5 }} more</span> }
      </p>
    }
  `,
})
export class FlowLinks {
  readonly broker = input.required<BrokerId>();
  readonly cluster = input.required<string>();
  readonly vhost = input.required<string>();
  readonly name = input.required<string>();
  readonly kind = input<'queue' | 'exchange'>('queue');
  readonly labels = DirectionLabels;

  // One request when the page opens; the server answers from its snapshots (and the browser revalidates the ETag).
  private readonly all = toSignal(
    inject(Api)
      .flows()
      .pipe(
        map((r) => r.body.flows),
        catchError(() => of([] as Flow[])),
      ),
    { initialValue: [] as Flow[] },
  );

  /** Node ids as the server builds them; MSMQ names are case-insensitive and lower-cased in ids. */
  readonly flows = computed(() => {
    const name = this.broker() === 'msmq' ? this.name().toLowerCase() : this.name();
    const id = [this.broker(), this.cluster(), this.vhost(), this.kind(), name].join('|');
    return this.all().filter((f) => f.nodes.some((n) => n.id === id));
  });
}
