import { signal } from '@angular/core';

/** Filter state persisted per browser, so lists reopen the way the user left them. */
export function persistedSignal<T>(key: string, initial: T) {
  let value = initial;
  try {
    const stored = localStorage.getItem(`dqe:${key}`);
    if (stored !== null) value = JSON.parse(stored) as T;
  } catch {
    // Storage unavailable: fall back to the default.
  }
  const s = signal<T>(value);
  const set = s.set.bind(s);
  s.set = (v: T) => {
    set(v);
    try {
      localStorage.setItem(`dqe:${key}`, JSON.stringify(v));
    } catch {
      // Ignore quota/private mode errors.
    }
  };
  return s;
}

/** Case-insensitive "all words present" match. */
export function matchesSearch(text: string, search: string): boolean {
  if (!search) return true;
  const haystack = text.toLowerCase();
  return search
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => haystack.includes(word));
}
