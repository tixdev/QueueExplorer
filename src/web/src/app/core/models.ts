export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export interface ClusterAccess {
  name: string;
  description?: string;
  actions: string[];
}

export interface Me {
  username: string;
  displayName: string;
  groups: string[];
  globalActions: string[];
  clusters: ClusterAccess[];
}

export interface SnapshotInfo {
  fetchedAt: string;
  fetchMs: number;
  lastErrorAt?: string;
  lastError?: string;
  /** What the RabbitMQ service account is not allowed to see on this cluster. */
  limitations: string[];
}

export interface Rate {
  rate: number;
}

export interface MessageStats {
  publishDetails?: Rate;
  deliverGetDetails?: Rate;
  ackDetails?: Rate;
  redeliverDetails?: Rate;
}

export interface Overview {
  clusterName?: string;
  rabbitmqVersion?: string;
  erlangVersion?: string;
  productName?: string;
  productVersion?: string;
  queueTotals?: { messages?: number; messagesReady?: number; messagesUnacknowledged?: number };
  objectTotals?: { connections?: number; channels?: number; exchanges?: number; queues?: number; consumers?: number };
  messageStats?: MessageStats;
}

export interface RmqNode {
  name: string;
  type?: string;
  running: boolean;
  uptime?: number;
  processors?: number;
  memUsed?: number;
  memLimit?: number;
  memAlarm?: boolean;
  diskFree?: number;
  diskFreeLimit?: number;
  diskFreeAlarm?: boolean;
  fdUsed?: number;
  fdTotal?: number;
  socketsUsed?: number;
  socketsTotal?: number;
  procUsed?: number;
  procTotal?: number;
  runQueue?: number;
}

export interface OverviewResponse {
  snapshot: SnapshotInfo;
  overview?: Overview;
  nodes?: RmqNode[];
}

export interface Queue {
  vhost: string;
  name: string;
  type?: string;
  state?: string;
  durable: boolean;
  autoDelete: boolean;
  exclusive: boolean;
  policy?: string;
  messages?: number;
  ready?: number;
  unacked?: number;
  consumers?: number;
  arguments?: Record<string, JsonValue>;
  canBrowse: boolean;
}

export interface Exchange {
  vhost: string;
  name: string;
  type: string;
  durable: boolean;
  autoDelete: boolean;
  internal: boolean;
  arguments?: Record<string, JsonValue>;
}

export interface Binding {
  vhost: string;
  source: string;
  destination: string;
  destinationType: 'queue' | 'exchange';
  routingKey: string;
  arguments?: Record<string, JsonValue>;
}

export interface Topology {
  snapshot: SnapshotInfo;
  vhosts: string[];
  queues: Queue[];
  exchanges: Exchange[];
  bindings: Binding[];
}

export interface Shovel {
  name: string;
  vhost?: string;
  type?: string;
  state: string;
  node?: string;
  timestamp?: string;
  source?: string;
  destination?: string;
  sourceUri?: string;
  destinationUri?: string;
  reason?: string;
  definition?: Record<string, JsonValue>;
}

export interface QueueDetail {
  queue: {
    name: string;
    vhost: string;
    type?: string;
    messages?: number;
    consumers?: number;
    messageStats?: MessageStats;
    /** Quorum queues only; -1 means unlimited. */
    deliveryLimit?: number;
  };
  bindings: Binding[];
}

export type BodyFormat = 'json' | 'xml' | 'text' | 'binary';

export interface FormattedBody {
  format: BodyFormat;
  text?: string;
  base64?: string;
  truncated: boolean;
}

export interface MessageTypeName {
  fullName: string;
  name: string;
  namespace?: string;
  assembly?: string;
  version?: string;
}

export interface HeaderEntry {
  key: string;
  label: string;
  value?: string;
}

export interface HeaderGroup {
  name: string;
  headers: HeaderEntry[];
}

export interface NServiceBusFailure {
  exceptionType?: string;
  message?: string;
  source?: string;
  stackTrace?: string;
  innerExceptionType?: string;
  failedQueue?: string;
  timeOfFailure?: string;
  processingEndpoint?: string;
  processingMachine?: string;
  /** Handler or saga that threw. */
  handler?: string;
  /** Only present while the message is still being retried. */
  delayedRetries?: number;
  lastDelayedRetryAt?: string;
  /** Application entries of Exception.Data. */
  data: Record<string, string | null>;
}

export interface NServiceBusInfo {
  kind: 'message' | 'failedMessage' | 'controlMessage' | 'subscription';
  messageId?: string;
  intent?: string;
  messageTypes: MessageTypeName[];
  conversationId?: string;
  correlationId?: string;
  relatedTo?: string;
  contentType?: string;
  nServiceBusVersion?: string;
  timeSent?: string;
  deliverAt?: string;
  timeToBeReceived?: string;
  originatingEndpoint?: string;
  originatingMachine?: string;
  originatingHost?: string;
  replyToAddress?: string;
  saga?: { sagaId?: string; sagaType?: string; originatingSagaId?: string; originatingSagaType?: string };
  failure?: NServiceBusFailure;
  headerGroups: HeaderGroup[];
}

export interface BrowsedMessage {
  index: number;
  /** Stable identity used to select messages for move/copy/export. */
  key: string;
  redelivered: boolean;
  exchange: string;
  routingKey: string;
  payloadBytes: number;
  properties: Record<string, JsonValue>;
  headers: Record<string, JsonValue>;
  contentType?: string;
  body: FormattedBody;
  nServiceBus?: NServiceBusInfo;
  /** Staging area only. */
  stagedId?: number;
  discarded?: boolean;
  lastPublishedAt?: string;
  lastPublishedTo?: string;
  broker?: string;
  editedAt?: string;
  editedBy?: string;
}

export interface HeaderEdit {
  key: string;
  value?: string | null;
  /** Leave the original (typed) value untouched. */
  keep?: boolean;
}

export interface PropertiesEdit {
  contentType?: string | null;
  contentEncoding?: string | null;
  deliveryMode?: number | null;
  priority?: number | null;
  correlationId?: string | null;
  replyTo?: string | null;
  expiration?: string | null;
  messageId?: string | null;
  type?: string | null;
  appId?: string | null;
}

export interface EditStagedMessageRequest {
  properties: PropertiesEdit;
  headers: HeaderEdit[];
  /** New UTF-8 body; omitted to keep the current one. */
  body?: string | null;
}

export const Brokers: { id: string; label: string }[] = [
  { id: 'rabbitmq', label: 'RabbitMQ' },
  { id: 'ibmmq', label: 'IBM MQ' },
  { id: 'kafka', label: 'Kafka' },
];

export function brokerLabel(id: string | undefined): string {
  return Brokers.find((b) => b.id === id)?.label ?? id ?? '';
}

export interface OperationFailure {
  key: string;
  reason: string;
}

export interface TransferOutcome {
  scanned: number;
  selected: number;
  succeeded: number;
  failures: OperationFailure[];
}

export interface TransferRequest {
  vhost: string;
  queue: string;
  mode: 'move' | 'copy';
  count?: number;
  keys?: string[];
  destinationVhost?: string;
  destinationQueue?: string;
  retryToFailedQueue?: boolean;
  removeFailureHeaders?: boolean;
}

export interface StagingBatch {
  id: number;
  name: string;
  createdBy: string;
  createdAt: string;
  origin: string;
  originCluster?: string;
  originVhost?: string;
  originQueue?: string;
  messages: number;
  discarded: number;
  published: number;
  broker: string;
}

export interface StagingBatchDetail {
  batch: StagingBatch;
  messages: BrowsedMessage[];
}

export interface PublishResult {
  requested: number;
  published: number;
  failures: OperationFailure[];
}

export interface BrowseResponse {
  queueType?: string;
  deliveryLimit?: number;
  queueDepth?: number;
  warnings: string[];
  messages: BrowsedMessage[];
}

export interface AuditEntry {
  id: number;
  timestamp: string;
  user: string;
  action: string;
  outcome: 'success' | 'denied' | 'failed';
  cluster?: string;
  vhost?: string;
  resource?: string;
  details?: string;
}

export const Actions = {
  overviewRead: 'overview.read',
  nodesRead: 'nodes.read',
  queuesRead: 'queues.read',
  exchangesRead: 'exchanges.read',
  shovelsRead: 'shovels.read',
  messagesBrowse: 'messages.browse',
  messagesMove: 'messages.move',
  messagesPublish: 'messages.publish',
  messagesExport: 'messages.export',
  messagesDelete: 'messages.delete',
  queuesPurge: 'queues.purge',
  stagingRead: 'staging.read',
  stagingWrite: 'staging.write',
  auditRead: 'audit.read',
} as const;

export interface TrendSample {
  at: string;
  messages: number;
  ready: number;
  unacked: number;
  errorMessages: number;
}

export interface TrendResponse {
  intervalSeconds: number;
  samples: TrendSample[];
}

export interface FailureGroup {
  endpoint?: string;
  exceptionType: string;
  count: number;
  lastFailure?: string;
  sampleMessage?: string;
  handler?: string;
}

export interface QueueFailureAnalysis {
  vhost: string;
  queue: string;
  analyzedAt: string;
  analyzedBy: string;
  depthWhenAnalyzed: number;
  analyzed: number;
  skipped?: string;
  groups: FailureGroup[];
}

export interface ErrorQueueSummary {
  vhost: string;
  queue: string;
  messages: number;
  canAnalyze: boolean;
  analysis?: QueueFailureAnalysis;
}
