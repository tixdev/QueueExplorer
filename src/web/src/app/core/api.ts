import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import {
  AuditEntry,
  BrowseResponse,
  BrowsedMessage,
  EditStagedMessageRequest,
  ErrorQueueSummary,
  FlowsResponse,
  TrendResponse,
  Me,
  OverviewResponse,
  PublishResult,
  QueueDetail,
  Shovel,
  SnapshotInfo,
  StagingBatch,
  StagingBatchDetail,
  Topology,
  TransferOutcome,
  TransferRequest,
} from './models';

/** A response body together with its ETag, so unchanged (304-revalidated) data can be skipped. */
export interface Tagged<T> {
  etag: string | null;
  body: T;
}

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  me(): Observable<Me> {
    return this.http.get<Me>('/api/me');
  }

  login(username: string, password: string): Observable<void> {
    return this.http.post<void>('/api/auth/login', { username, password });
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', {});
  }

  // Snapshot endpoints answer with an ETag and `no-cache`: the browser revalidates on its own and a 304
  // comes back as the cached body with the same ETag.
  overview(cluster: string): Observable<Tagged<OverviewResponse>> {
    return this.tagged(this.http.get<OverviewResponse>(`${base(cluster)}/overview`, { observe: 'response' }));
  }

  topology(cluster: string): Observable<Tagged<Topology>> {
    return this.tagged(this.http.get<Topology>(`${base(cluster)}/topology`, { observe: 'response' }));
  }

  shovels(cluster: string): Observable<Tagged<Shovel[]>> {
    return this.tagged(this.http.get<Shovel[]>(`${base(cluster)}/shovels`, { observe: 'response' }));
  }

  /** With a queue group, the trend of that group's queues only. */
  trend(cluster: string, minutes: number, group?: string): Observable<TrendResponse> {
    return this.http.get<TrendResponse>(`${base(cluster)}/trend`, { params: group ? { minutes, group } : { minutes } });
  }

  /** Migration flows across brokers (shovels, bindings, MSMQ ⇄ RabbitMQ bridge), built from the snapshots. */
  flows(): Observable<Tagged<FlowsResponse>> {
    return this.tagged(this.http.get<FlowsResponse>('/api/flows', { observe: 'response' }));
  }

  errorQueues(cluster: string): Observable<ErrorQueueSummary[]> {
    return this.http.get<ErrorQueueSummary[]>(`${base(cluster)}/errors`);
  }

  analyzeErrors(cluster: string, vhost?: string, queue?: string): Observable<ErrorQueueSummary[]> {
    return this.http.post<ErrorQueueSummary[]>(`${base(cluster)}/errors/analyze`, { vhost, queue });
  }

  queue(cluster: string, vhost: string, name: string): Observable<QueueDetail> {
    return this.http.get<QueueDetail>(`${base(cluster)}/queue`, { params: { vhost, name } });
  }

  refresh(cluster: string): Observable<SnapshotInfo> {
    return this.http.post<SnapshotInfo>(`${base(cluster)}/refresh`, {});
  }

  browse(cluster: string, vhost: string, queue: string, count: number, acceptRequeueSideEffects: boolean): Observable<BrowseResponse> {
    return this.http.post<BrowseResponse>(`${base(cluster)}/messages/browse`, { vhost, queue, count, acceptRequeueSideEffects });
  }

  transfer(cluster: string, request: TransferRequest): Observable<TransferOutcome> {
    return this.http.post<TransferOutcome>(`${base(cluster)}/messages/transfer`, request);
  }

  /** Downloads the selected messages of a queue as a .mqx file (the messages stay in the queue). */
  exportMessages(cluster: string, vhost: string, queue: string, keys?: string[], count?: number): Observable<void> {
    return this.download(this.http.post(`${base(cluster)}/messages/export`, { vhost, queue, keys, count }, { observe: 'response', responseType: 'blob' }));
  }

  deleteMessages(cluster: string, vhost: string, queue: string, keys?: string[], count?: number): Observable<{ deleted: number }> {
    return this.http.post<{ deleted: number }>(`${base(cluster)}/messages/delete`, { vhost, queue, keys, count });
  }

  purgeQueue(cluster: string, vhost: string, queue: string, confirmName: string): Observable<{ messagesBefore?: number }> {
    return this.http.post<{ messagesBefore?: number }>(`${base(cluster)}/queues/purge`, { vhost, queue, confirmName });
  }

  stage(cluster: string, vhost: string, queue: string, remove: boolean, keys?: string[], count?: number): Observable<StagingBatch> {
    return this.http.post<StagingBatch>(`${base(cluster)}/messages/stage`, { vhost, queue, remove, keys, count });
  }

  stagingBatches(broker?: string): Observable<StagingBatch[]> {
    return this.http.get<StagingBatch[]>('/api/staging', { params: broker ? { broker } : {} });
  }

  editStaged(batchId: number, messageId: number, request: EditStagedMessageRequest): Observable<BrowsedMessage> {
    return this.http.put<BrowsedMessage>(`/api/staging/${batchId}/messages/${messageId}`, request);
  }

  revertStaged(batchId: number, messageId: number): Observable<BrowsedMessage> {
    return this.http.post<BrowsedMessage>(`/api/staging/${batchId}/messages/${messageId}/revert`, {});
  }

  stagingBatch(id: number): Observable<StagingBatchDetail> {
    return this.http.get<StagingBatchDetail>(`/api/staging/${id}`);
  }

  importToStaging(file: File, name?: string): Observable<StagingBatch> {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<StagingBatch>('/api/staging/import', form, { params: name ? { name } : {} });
  }

  deleteStagingBatch(id: number): Observable<void> {
    return this.http.delete<void>(`/api/staging/${id}`);
  }

  discardStaged(id: number, ids: number[], discarded: boolean): Observable<unknown> {
    return this.http.post(`/api/staging/${id}/discard`, { ids, discarded });
  }

  exportStagingBatch(id: number): Observable<void> {
    return this.download(this.http.get(`/api/staging/${id}/export`, { observe: 'response', responseType: 'blob' }));
  }

  publishStaged(
    id: number,
    request: { cluster: string; vhost: string; queue?: string; retryToFailedQueue?: boolean; removeFailureHeaders?: boolean; ids?: number[] },
  ): Observable<PublishResult> {
    return this.http.post<PublishResult>(`/api/staging/${id}/publish`, request);
  }

  audit(before?: number, take = 100): Observable<AuditEntry[]> {
    const params: Record<string, number> = { take };
    if (before !== undefined) params['before'] = before;
    return this.http.get<AuditEntry[]>('/api/audit', { params });
  }

  /** Saves a file response through a temporary link, using the server-provided file name. */
  private download(response: Observable<HttpResponse<Blob>>): Observable<void> {
    return response.pipe(
      map((r) => {
        const disposition = r.headers.get('Content-Disposition') ?? '';
        const name = /filename\*=UTF-8''([^;]+)/.exec(disposition)?.[1] ?? /filename="?([^";]+)"?/.exec(disposition)?.[1] ?? 'messages.mqx';
        const url = URL.createObjectURL(r.body!);
        const link = document.createElement('a');
        link.href = url;
        link.download = decodeURIComponent(name);
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      }),
    );
  }

  private tagged<T>(response: Observable<HttpResponse<T>>): Observable<Tagged<T>> {
    return response.pipe(map((r) => ({ etag: r.headers.get('ETag'), body: r.body as T })));
  }
}

function base(cluster: string): string {
  return `/api/clusters/${encodeURIComponent(cluster)}`;
}

export const QUORUM_CONFIRMATION = 'quorum-requeue-confirmation-required';
