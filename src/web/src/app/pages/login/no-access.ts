import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Auth } from '../../core/auth';

@Component({
  selector: 'app-no-access',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="login">
      <div class="card login-card">
        <h1>No access</h1>
        <p>The user <strong>{{ auth.me()?.displayName }}</strong> has no permissions on any cluster.</p>
        <p class="muted">Ask an administrator to assign you a role.</p>
        <button (click)="logout()">Sign out</button>
      </div>
    </main>
  `,
})
export class NoAccess {
  readonly auth = inject(Auth);
  private readonly router = inject(Router);

  logout(): void {
    this.auth.logout().subscribe(() => void this.router.navigate(['/login']));
  }
}
