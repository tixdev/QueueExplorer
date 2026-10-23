export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export type BrokerId = 'rabbitmq' | 'ibmmq' | 'msmq' | 'kafka';

/** A connection the user can open: a RabbitMQ cluster, an IBM MQ queue manager or an MSMQ machine. */
export interface ClusterAccess {
  name: string;
  description?: string;
  broker: BrokerId;
  actions: string[];
}

export interface Me {
  username: string;
  displayName: string;
  groups: string[];
  globalActions: string[];
  clusters: ClusterAccess[];
  queueGroups: QueueGroup[];
}

/** Glob patterns (`*`, `?`, case-insensitive); an unset field matches everything. For IBM MQ the vhost is the queue manager. */
export interface QueueGroupRule {
  broker?: BrokerId;
  cluster?: string;
  vhost?: string;
  queue?: string;
}

/** A named selection of queues across brokers (e.g. "T1 CH"); a queue belongs to it when any rule matches. */
export interface QueueGroup {
  name: string;
  description?: string;
  rules: QueueGroupRule[];
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

export interface MqQueueManager {
  name: string;
  description?: string;
  commandLevel?: number;
  version?: string;
  platform?: string;
  deadLetterQueue?: string;
}

export interface OverviewResponse {
  snapshot: SnapshotInfo;
  overview?: Overview;
  nodes?: RmqNode[];
  /** IBM MQ only. */
  queueManager?: MqQueueManager;
  /** MSMQ only. */
  msmq?: MsmqMachine;
}

/** An MSMQ machine read through the agent. */
export interface MsmqMachine {
  name: string;
  host: string;
  agent: string;
  /** performance counters (one call) or queue management (queue by queue, slower). */
  depthSource: string;
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
  // IBM MQ only: vhost is the queue manager, messages the current depth, unacked the uncommitted messages,
  // consumers the open input count.
  maxDepth?: number;
  openOutput?: number;
  inhibitGet?: boolean;
  inhibitPut?: boolean;
  /** Alias: target queue; remote: queue@qmgr via xmitq. */
  target?: string;
  description?: string;
  lastPut?: string;
  lastGet?: string;
  oldestMessageAgeSeconds?: number;
  transmission?: boolean;
  // MSMQ only: vhost is the machine, type the kind (private, outgoing, deadletter), state the outgoing connection state.
  bytes?: number;
  journalMessages?: number;
  /** Dead-letter queues are counted by peeking up to a limit: there are at least `messages`. */
  atLeast?: boolean;
  nextHops?: string[];
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
  ibmMq?: MqMessageView;
  msmq?: MsmqMessageView;
  /** When the message was produced: NServiceBus TimeSent, else the AMQP timestamp, else the IBM MQ put time. */
  sentAt?: string;
}

/** MSMQ part of a message: native properties and the extension. */
export interface MsmqMessageView {
  id: string;
  label?: string;
  class?: number;
  /** For a dead-lettered message, why it was dead-lettered. */
  classDescription?: string;
  properties: MqProperty[];
  /** NServiceBus headers, text or binary; absent when the extension is empty. */
  extension?: string;
  extensionText?: string;
}

export interface MqProperty {
  name: string;
  value?: string;
  type?: string;
}

export interface MqHeader {
  /** MQ format name of the header (MQDEAD, MQHRF2, MQXMIT…). */
  format: string;
  title: string;
  fields: MqProperty[];
  folders: { name: string; xml: string; properties: MqProperty[] }[];
}

export interface PcfMessage {
  type: number;
  command: number;
  commandName: string;
  compCode: number;
  reason: number;
  reasonName: string;
  last: boolean;
  parameters: { id: number; name: string; value: string }[];
}

export interface MqMessageView {
  messageIdHex: string;
  messageIdText?: string;
  correlationIdHex: string;
  correlationIdText?: string;
  mqmd: MqProperty[];
  headers: MqHeader[];
  bodyFormat: string;
  bodyCcsid: string;
  warning?: string;
  pcf?: PcfMessage;
  /** First bytes of a body decoded as text, for the hex view. */
  rawBodyBase64?: string;
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
  { id: 'msmq', label: 'MSMQ' },
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
