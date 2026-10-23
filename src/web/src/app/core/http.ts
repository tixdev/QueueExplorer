import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, throwError } from 'rxjs';
import { Auth } from './auth';

/** Adds the anti-CSRF header the API requires and sends the user to login when the session expires. */
export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const router = inject(Router);
  const auth = inject(Auth);
  const request = req.url.startsWith('/api') ? req.clone({ setHeaders: { 'X-DQE-Client': '1' } }) : req;
  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && !req.url.startsWith('/api/auth/login')) {
        auth.clear();
        void router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
      }
      return throwError(() => error);
    }),
  );
};

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);
  return auth.ensureLoaded().pipe(
    map((ok) => (ok ? true : router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } }))),
  );
};

export function errorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) return 'Server not reachable.';
    if (error.status === 403) return 'You are not allowed to do this.';
    if (error.status === 404) return 'Not found.';
    const detail = (error.error as { detail?: string; title?: string } | null)?.detail ?? (error.error as { title?: string } | null)?.title;
    return detail ?? `Error ${error.status}`;
  }
  return error instanceof Error ? error.message : String(error);
}
