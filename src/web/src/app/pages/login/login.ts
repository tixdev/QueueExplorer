import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Auth } from '../../core/auth';
import { errorMessage } from '../../core/http';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="login">
      <form class="card login-card" (ngSubmit)="submit()">
        <h1 class="brand"><img src="favicon.svg" alt="" width="32" height="32" /> DynaQueueExplorer</h1>
        <p class="muted">Test sign-in with local users</p>
        <label>User <input name="username" [(ngModel)]="username" autocomplete="username" required autofocus /></label>
        <label>Password <input name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required /></label>
        @if (error()) {
          <p class="error">{{ error() }}</p>
        }
        <button type="submit" class="primary" [disabled]="busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
      </form>
    </main>
  `,
})
export class Login {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  username = '';
  password = '';
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  submit(): void {
    this.busy.set(true);
    this.error.set(null);
    this.auth.login(this.username, this.password).subscribe({
      next: () => void this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/'),
      error: (e) => {
        this.busy.set(false);
        this.error.set((e as { status?: number }).status === 401 ? 'Invalid user name or password.' : errorMessage(e));
      },
    });
  }
}
