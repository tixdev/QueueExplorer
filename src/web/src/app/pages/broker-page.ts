import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, Type, effect, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { ClusterStore } from '../core/cluster-store';
import { BrokerId } from '../core/models';

export type PageLoaders = Partial<Record<BrokerId, () => Promise<Type<unknown>>>>;

/**
 * Shows the page of the selected connection's broker, so every broker keeps its own page instead of conditionals
 * scattered in shared views. Each page is loaded lazily, and only for the broker in use.
 */
@Component({
  selector: 'app-broker-page',
  imports: [NgComponentOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page(); as p) {
      <ng-container *ngComponentOutlet="p" />
    } @else if (unsupported()) {
      <p class="muted">This page is not available for this kind of connection.</p>
    }
  `,
})
export class BrokerPage {
  private readonly store = inject(ClusterStore);
  private readonly loaders = inject(ActivatedRoute).snapshot.data['pages'] as PageLoaders;

  readonly page = signal<Type<unknown> | null>(null);
  readonly unsupported = signal(false);
  private request = 0;

  constructor() {
    effect(() => {
      const load = this.loaders[this.store.broker()];
      const request = ++this.request;
      this.page.set(null);
      this.unsupported.set(!load);
      // A slower load for a previous broker must not replace the current page.
      void load?.().then((component) => request === this.request && this.page.set(component));
    });
  }
}

/** Route data for a page that differs per broker. */
export function brokerPages(pages: PageLoaders): { pages: PageLoaders } {
  return { pages };
}
