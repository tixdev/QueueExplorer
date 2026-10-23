import { DestroyRef, Signal, inject, signal } from '@angular/core';
import { EMPTY, Observable, Subject, catchError, exhaustMap, fromEvent, map, merge, startWith, switchMap, timer } from 'rxjs';
import { Tagged } from './api';
import { errorMessage } from './http';

export interface Polled<T> {
  value: Signal<T | undefined>;
  /** When the server last confirmed the value, including unchanged (304) answers. */
  checkedAt: Signal<number | null>;
  error: Signal<string | null>;
  loading: Signal<boolean>;
  reload(): void;
}

/**
 * Polls an ETag-tagged endpoint while the browser tab is visible. A request is never started while
 * the previous one is still running, and an unchanged ETag does not emit, so views do not recompute.
 */
export function poll<T>(load: () => Observable<Tagged<T>>, intervalMs: number): Polled<T> {
  const value = signal<T | undefined>(undefined);
  const error = signal<string | null>(null);
  const loading = signal(true);
  const checkedAt = signal<number | null>(null);
  const manual = new Subject<void>();
  let lastEtag: string | null | undefined;

  const visible = fromEvent(document, 'visibilitychange').pipe(
    startWith(null),
    map(() => document.visibilityState === 'visible'),
  );
  const ticks = merge(visible.pipe(switchMap((v) => (v ? timer(0, intervalMs) : EMPTY))), manual);

  const subscription = ticks
    .pipe(
      exhaustMap(() =>
        load().pipe(
          catchError((e: unknown) => {
            error.set(errorMessage(e));
            loading.set(false);
            return EMPTY;
          }),
        ),
      ),
    )
    .subscribe((result) => {
      error.set(null);
      loading.set(false);
      checkedAt.set(Date.now());
      if (result.etag !== null && result.etag === lastEtag) return;
      lastEtag = result.etag;
      value.set(result.body);
    });

  inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  return {
    value: value.asReadonly(),
    checkedAt: checkedAt.asReadonly(),
    error: error.asReadonly(),
    loading: loading.asReadonly(),
    reload: () => manual.next(),
  };
}
