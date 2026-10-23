import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NumberPipe } from './format';
import { Subscriber, eventName } from './nsb-topology';

const ROW = 48;
const GAP = 8;
const WIRE = 56;

/** An event and its subscribing endpoints, with connectors: who receives what is published to this exchange. */
@Component({
  selector: 'app-event-flow',
  imports: [RouterLink, NumberPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="event-flow" [style.height.px]="height()">
      <div class="flow-event" [style.top.px]="eventTop()">
        <span class="flow-kind">event</span>
        <strong>{{ title().name }}</strong>
        <span class="muted small mono ellipsis-block" [title]="title().ns">{{ title().ns }}</span>
      </div>
      <svg class="flow-wires" [attr.width]="wire" [attr.height]="height()" aria-hidden="true">
        @for (p of paths(); track $index) {
          <path [attr.d]="p" />
        }
      </svg>
      <ul class="flow-subscribers">
        @for (s of subscribers(); track s.endpoint) {
          <li [style.height.px]="row">
            <a class="flow-endpoint" [routerLink]="['/c', cluster(), 'queues', vhost(), s.endpoint]">
              <span class="mono ellipsis-block" [title]="s.endpoint">{{ s.endpoint }}</span>
              <span class="flow-meta">
                @if (s.queue; as q) {
                  {{ q.messages | num }} msg ·
                  @if (q.consumers === 0) {
                    <span [class.warn-text]="(q.messages ?? 0) > 0" [title]="(q.messages ?? 0) > 0 ? 'Messages are waiting and nothing consumes them' : ''">no consumers</span>
                  } @else { {{ q.consumers }} consumer{{ q.consumers === 1 ? '' : 's' }} }
                }
                @if (s.via.length) {
                  · <button class="link via" (click)="$event.preventDefault(); $event.stopPropagation(); openEvent.emit(s.via[s.via.length - 1])"
                       [title]="'Subscribed through ' + s.via.join(' → ')">via {{ shortName(s.via[s.via.length - 1]) }}</button>
                }
              </span>
            </a>
          </li>
        }
      </ul>
    </div>
    @if (!subscribers().length) {
      <p class="muted small">No subscribers: messages published to this event are dropped by RabbitMQ.</p>
    }
  `,
})
export class EventFlow {
  readonly cluster = input.required<string>();
  readonly vhost = input.required<string>();
  readonly event = input.required<string>();
  readonly subscribers = input.required<Subscriber[]>();
  readonly openEvent = output<string>();

  readonly row = ROW;
  readonly wire = WIRE;

  readonly title = computed(() => eventName(this.event()));
  readonly height = computed(() => Math.max(ROW, this.subscribers().length * (ROW + GAP) - GAP));
  readonly eventTop = computed(() => this.height() / 2 - 32);

  /** One curve per subscriber, from the event's mid-height to the middle of its row. */
  readonly paths = computed(() => {
    const mid = this.height() / 2;
    return this.subscribers().map((_, i) => {
      const y = i * (ROW + GAP) + ROW / 2;
      return `M0,${mid} C${WIRE / 2},${mid} ${WIRE / 2},${y} ${WIRE},${y}`;
    });
  });

  shortName(exchange: string): string {
    return eventName(exchange).name;
  }
}
