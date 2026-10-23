import { Pipe, PipeTransform } from '@angular/core';
import { JsonValue } from '../core/models';

const numberFormat = new Intl.NumberFormat('en-GB');
const dateTimeFormat = new Intl.DateTimeFormat('en-GB', { dateStyle: 'short', timeStyle: 'medium' });
const relativeFormat = new Intl.RelativeTimeFormat('en-GB', { numeric: 'auto' });

export function formatNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? '–' : numberFormat.format(value);
}

export function formatBytes(value: number | null | undefined): string {
  if (value === null || value === undefined) return '–';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = value;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v < 10 && i > 0 ? 1 : 0)} ${units[i]}`;
}

export function formatDateTime(value: string | null | undefined): string {
  return value ? dateTimeFormat.format(new Date(value)) : '–';
}

export function formatRelative(value: string | null | undefined, now = Date.now()): string {
  if (!value) return '–';
  const seconds = Math.round((new Date(value).getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 60) return relativeFormat.format(seconds, 'second');
  if (abs < 3600) return relativeFormat.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return relativeFormat.format(Math.round(seconds / 3600), 'hour');
  return relativeFormat.format(Math.round(seconds / 86400), 'day');
}

export function formatUptime(ms: number | null | undefined): string {
  if (!ms) return '–';
  const minutes = Math.floor(ms / 60000);
  const d = Math.floor(minutes / 1440);
  const h = Math.floor((minutes % 1440) / 60);
  const m = minutes % 60;
  return d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m`;
}

export function formatJsonValue(value: JsonValue | undefined): string {
  if (value === undefined || value === null) return '';
  return typeof value === 'string' ? value : JSON.stringify(value);
}

@Pipe({ name: 'num' })
export class NumberPipe implements PipeTransform {
  transform = formatNumber;
}

@Pipe({ name: 'bytes' })
export class BytesPipe implements PipeTransform {
  transform = formatBytes;
}

@Pipe({ name: 'datetime' })
export class DateTimePipe implements PipeTransform {
  transform = formatDateTime;
}

@Pipe({ name: 'relative' })
export class RelativePipe implements PipeTransform {
  /** Pass a changing `now` to keep the label current in templates. */
  transform(value: string | null | undefined, now?: number): string {
    return formatRelative(value, now);
  }
}

@Pipe({ name: 'uptime' })
export class UptimePipe implements PipeTransform {
  transform = formatUptime;
}

@Pipe({ name: 'jsonValue' })
export class JsonValuePipe implements PipeTransform {
  transform = formatJsonValue;
}

export async function copyToClipboard(text: string): Promise<void> {
  await navigator.clipboard.writeText(text);
}

/** NServiceBus delayed-delivery infrastructure, hidden by default in lists. */
export function isNsbInfrastructure(name: string): boolean {
  return name.startsWith('nsb.v2.delay-') || name.startsWith('nsb.delay-');
}
