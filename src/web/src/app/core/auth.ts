import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, map, of, switchMap, tap } from 'rxjs';
import { Api } from './api';
import { Me } from './models';

@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly api = inject(Api);
  private readonly current = signal<Me | null>(null);

  readonly me = this.current.asReadonly();
  readonly isAuthenticated = computed(() => this.current() !== null);

  /** Loads the current user once; resolves to false when the session is missing or expired. */
  ensureLoaded(): Observable<boolean> {
    if (this.current()) return of(true);
    return this.api.me().pipe(
      tap((me) => this.current.set(me)),
      map(() => true),
      catchError(() => of(false)),
    );
  }

  /** Logs in and loads the user in one go, so the shell renders without a further round-trip. */
  login(username: string, password: string): Observable<Me> {
    this.current.set(null);
    return this.api.login(username, password).pipe(
      switchMap(() => this.api.me()),
      tap((me) => this.current.set(me)),
    );
  }

  logout(): Observable<void> {
    return this.api.logout().pipe(tap(() => this.current.set(null)));
  }

  clear(): void {
    this.current.set(null);
  }

  can(cluster: string, action: string): boolean {
    return this.current()?.clusters.find((c) => c.name === cluster)?.actions.includes(action) ?? false;
  }

  canGlobal(action: string): boolean {
    return this.current()?.globalActions.includes(action) ?? false;
  }
}
