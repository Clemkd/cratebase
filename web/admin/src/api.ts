/**
 * HTTP client for the console.
 *
 * Ancestor of `@cratebase/client`, the public SDK: same error shape, same conventions. What lives
 * here will eventually be extracted, so nothing console-specific should creep in.
 */

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly detail: string,
    readonly errors?: Record<string, string[]>,
  ) {
    super(detail)
    this.name = 'ApiError'
  }

  get isUnauthenticated() {
    return this.status === 401
  }

  get isForbidden() {
    return this.status === 403
  }

  get isNotFound() {
    return this.status === 404
  }

  get isConflict() {
    return this.status === 409
  }

  /** First validation error message, for a compact display. */
  get firstValidationMessage(): string | undefined {
    if (!this.errors) return undefined
    const [field, messages] = Object.entries(this.errors)[0] ?? []
    return field && messages?.[0] ? `${field}: ${messages[0]}` : undefined
  }
}

/** Human-readable message for a failure, whatever its nature. */
export function describeFailure(failure: unknown): string {
  if (failure instanceof ApiError) {
    return failure.firstValidationMessage ?? failure.detail
  }

  if (failure instanceof Error) {
    return failure.message
  }

  return String(failure)
}

/** Validation errors carried by a failure, indexed by field. */
export function validationErrors(failure: unknown): Record<string, string[]> {
  return failure instanceof ApiError ? (failure.errors ?? {}) : {}
}

/**
 * Current session.
 *
 * `sessionStorage` and not `localStorage`: the token disappears when the tab closes, which limits
 * the exposure window on a shared machine. Since the token is revocable server-side, signing out
 * truly destroys it — here as well as in the database.
 */
const TOKEN_KEY = 'cratebase.token'

type ExpiryListener = () => void

const expiryListeners = new Set<ExpiryListener>()

export const session = {
  get token(): string | null {
    return globalThis.sessionStorage?.getItem(TOKEN_KEY) ?? null
  },

  set token(value: string | null) {
    if (value) globalThis.sessionStorage?.setItem(TOKEN_KEY, value)
    else globalThis.sessionStorage?.removeItem(TOKEN_KEY)
  },

  /**
   * Subscribes to token expiry.
   *
   * A 401 can happen on any call — token revoked from another tab, permissions withdrawn.
   * Handling it in every screen would inevitably leave a path uncovered, so the purge and the
   * return to the login screen are triggered here, once, for everyone.
   */
  onExpired(listener: ExpiryListener): () => void {
    expiryListeners.add(listener)
    return () => expiryListeners.delete(listener)
  },
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = session.token

  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init.headers,
    },
  })

  if (response.status === 204) return undefined as T

  const payload: unknown = await response.json().catch(() => null)

  if (!response.ok) {
    // A 401 on a call carrying a token means that token is no longer valid: purge it before
    // propagating, otherwise the next screen would resend it and get refused in turn.
    if (response.status === 401 && token) {
      session.token = null
      expiryListeners.forEach((listener) => listener())
    }

    throw toApiError(response.status, payload)
  }

  return payload as T
}

interface ProblemDetails {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

function toApiError(status: number, payload: unknown): ApiError {
  const problem = (typeof payload === 'object' && payload !== null ? payload : {}) as ProblemDetails

  return new ApiError(
    status,
    problem.detail ?? problem.title ?? `HTTP error ${status}`,
    problem.errors,
  )
}

export type FieldType =
  | 'Text' | 'Editor' | 'Number' | 'Bool' | 'Email' | 'Url'
  | 'Date' | 'AutoDate' | 'Select' | 'File' | 'Relation' | 'Json' | 'GeoPoint'

export interface FieldOptions {
  min?: number | null
  max?: number | null
  pattern?: string | null
  integerOnly?: boolean
  values?: string[]
  targetCollection?: string | null
  cascadeDelete?: boolean
  maxFileSize?: number | null
  mimeTypes?: string[]
  thumbSizes?: string[]
  protected?: boolean
  onCreate?: boolean
  onUpdate?: boolean
}

export interface Field {
  id: string
  name: string
  type: FieldType
  required: boolean
  isSystem: boolean
  hidden: boolean
  maxSelect: number
  options: FieldOptions
  multiple: boolean
}

export interface AccessRules {
  list: string | null
  view: string | null
  create: string | null
  update: string | null
  delete: string | null
  manage: string | null
}

export type RuleAction = keyof AccessRules

export interface CollectionIndex {
  name: string
  fields: string[]
  unique: boolean
}

export type CollectionKind = 'Base' | 'Auth' | 'View'

export interface Collection {
  id: string
  name: string
  kind: CollectionKind
  fields: Field[]
  indexes: CollectionIndex[]
  rules: AccessRules
  isSystem: boolean
  created: string
  updated: string
}

/** Field payload as expected by the server. */
export interface FieldPayload {
  /**
   * ⚠️ Omitted for a new field, sent back as-is for an existing field. This is what distinguishes
   * a rename — which preserves the data — from a replacement, which destroys it.
   */
  id?: string
  name: string
  type: FieldType
  required: boolean
  maxSelect: number
  options: FieldOptions
}

/** Collection payload as expected by the server. */
export interface CollectionPayload {
  name: string
  type: CollectionKind
  fields: FieldPayload[]
  indexes: CollectionIndex[]
  rules: AccessRules
}

export interface Page<T> {
  page: number
  perPage: number
  totalItems: number
  totalPages: number
  items: T[]
}

export type RecordValue = Record<string, unknown>

export interface Identity {
  id: string
  collectionName: string
  isSuperuser: boolean
  permissions: string[]
  fields: Record<string, unknown>
}

export interface Health {
  status: string
  engine: string
  collections: number
}

export type LogLevel = 'Debug' | 'Info' | 'Warning' | 'Error'

/** Levels from lowest to most severe. The order backs the "at least this level" filter. */
export const LOG_LEVELS: LogLevel[] = ['Debug', 'Info', 'Warning', 'Error']

export interface LogEntry {
  id: string
  created: string
  level: LogLevel
  message: string
  method: string
  url: string
  /** Zero for an application entry, which doesn't answer any request. */
  status: number
  /** Processing duration, in milliseconds. */
  duration: number
  authCollection: string
  authId: string
  ip: string
  userAgent: string
  referer: string
  data: Record<string, unknown>
}

export interface LogListParams {
  page?: number
  perPage?: number
  /**
   * Levels to keep. Empty or absent: all.
   *
   * A set rather than a lower bound: "warnings without errors" is a common operational question
   * that a minimum severity cannot express.
   */
  levels?: LogLevel[]
  /** Fragment searched for in the message or URL. */
  q?: string
  method?: string
  status?: number
  from?: string
  to?: string
  sort?: 'created' | '-created'
}

export type LogGranularity = 'Minute' | 'Hour' | 'Day'

export interface LogBucket {
  bucket: string
  level: LogLevel
  count: number
}

export interface LogStats {
  granularity: LogGranularity
  from: string | null
  to: string | null
  items: LogBucket[]
}

/** An object from the store, mapped back into the collection model. */
export interface StoredObject {
  key: string
  collection: string
  recordId: string
  fileName: string
  size: number
  contentType: string
  isThumb: boolean
  /** No record references this file. */
  orphan: boolean
}

export interface StorageObjectsParams {
  page?: number
  perPage?: number
  collection?: string
  q?: string
  /** `files`, `thumbs`, or empty for both. */
  kind?: string
  /** Show only objects that no record references. */
  orphans?: boolean
}

/**
 * Description of the file store.
 *
 * No secret key appears here: the store is decided by the host configuration, and the console
 * only ever reads it, never writes it. `accessKeyHint` only carries the last four characters of
 * the access key — enough to recognize which one is active.
 */
export interface Storage {
  kind: 'local' | 's3'
  name: string
  directory: string
  bucket: string
  endpoint: string
  publicEndpoint: string
  region: string
  forcePathStyle: boolean
  accessKeyHint: string
  hasSecretKey: boolean
  presignedUrls: boolean
  objects: { files: number; thumbs: number; fileBytes: number; thumbBytes: number }
}

/**
 * Measured usage of the instance.
 *
 * A capacity of zero means "unknown", not "none": neither PostgreSQL nor S3 expose a limit in a
 * portable way, so the screen must then show a volume without a gauge rather than an invented
 * gauge.
 */
export interface Usage {
  host: { available: boolean; path: string; totalBytes: number; freeBytes: number }
  database: { engine: string; bytes: number; capacityBytes: number; onHostDisk: boolean }
  files: {
    kind: 'local' | 's3'
    bytes: number
    objects: number
    capacityBytes: number
    onHostDisk: boolean
  }
}

export interface StorageProbeStep {
  name: string
  state: 'ok' | 'skipped' | 'failed'
  detail: string
  milliseconds: number
}

export interface StorageProbe {
  ok: boolean
  steps: StorageProbeStep[]
}

export interface LogSettings {
  enabled: boolean
  retentionDays: number
  minLevel: LogLevel
  logIp: boolean
}

export interface RealtimeSettings {
  enabled: boolean
  maxClients: number
}

export interface AppSettings {
  appName: string
  appUrl: string
  logs: LogSettings
  realtime: RealtimeSettings
}

/**
 * Submitted settings.
 *
 * Partial end to end: the server leaves in place whatever the payload doesn't mention. Sending
 * the full object would reset to default any settings a given screen doesn't know about.
 */
export interface SettingsPayload {
  realtime?: Partial<RealtimeSettings>
  appName?: string
  appUrl?: string
  logs?: Partial<LogSettings>
}

export interface OAuthProvider {
  name: string
  displayName: string
  enabled: boolean
  authorizationUrl: string
  scopes: string[]
  usePkce: boolean
}

export interface Instance {
  appName: string
  appUrl: string
  version: string
  runtime: string
  startedAt: string
  uptimeSeconds: number
  engine: string
  storage: string
  dataDirectory: string
  apiPrefix: string
  collections: { total: number; data: number; auth: number; view: number; system: number }
  logs: { total: number; dropped: number; enabled: boolean; retentionDays: number }
  providers: OAuthProvider[]
}

export interface RecordListParams {
  page?: number
  perPage?: number
  filter?: string
  sort?: string
  fields?: string
  skipTotal?: boolean
}

function toQuery(params: RecordListParams): string {
  const entries: [string, string][] = []

  if (params.page !== undefined) entries.push(['page', String(params.page)])
  if (params.perPage !== undefined) entries.push(['perPage', String(params.perPage)])
  if (params.filter) entries.push(['filter', params.filter])
  if (params.sort) entries.push(['sort', params.sort])
  if (params.fields) entries.push(['fields', params.fields])
  if (params.skipTotal) entries.push(['skipTotal', '1'])

  const query = new URLSearchParams(entries).toString()

  return query ? `?${query}` : ''
}

/** Assembles a query string, skipping anything empty. */
function toSearch(params: Record<string, string | number | undefined>): string {
  const entries = Object.entries(params)
    .filter((entry): entry is [string, string | number] => entry[1] !== undefined && entry[1] !== '')
    .map(([key, value]): [string, string] => [key, String(value)])

  const query = new URLSearchParams(entries).toString()

  return query ? `?${query}` : ''
}

/**
 * Log query string.
 *
 * Levels travel comma-separated under a single `level` key: `URLSearchParams` can repeat a key,
 * but an address with four `level=` entries is unreadable in an address bar — and that's exactly
 * where it gets read again when sharing a log link.
 */
function logSearch(
  params: LogListParams & { granularity?: LogGranularity; stats?: number },
): string {
  const { levels, ...rest } = params

  return toSearch({
    ...rest,
    level: levels !== undefined && levels.length > 0 ? levels.join(',') : undefined,
  })
}

export const api = {
  health: () => request<Health>('/health'),

  /** Instance state: engine, storage, version, usage. Reserved for superusers. */
  instance: () => request<Instance>('/instance'),

  /**
   * Disk, database, and file usage.
   *
   * Separate from `/instance` because it genuinely costs something: it queries the engine and
   * walks the store. A screen that only wants the instance name shouldn't have to pay that price.
   */
  usage: () => request<Usage>('/usage'),

  settings: {
    get: () => request<AppSettings>('/settings'),

    update: (payload: SettingsPayload) =>
      request<AppSettings>('/settings', { method: 'PATCH', body: JSON.stringify(payload) }),
  },

  logs: {
    list: (params: LogListParams = {}) => request<Page<LogEntry>>(`/logs${logSearch(params)}`),

    /**
     * Page and histogram in a single call.
     *
     * ⚠️ Deliberately inseparable: two distinct calls would each drain the server's write buffer,
     * so the second would see entries the first didn't — and the chart would report a total the
     * table below it doesn't show.
     */
    listWithStats: (params: LogListParams & { granularity?: LogGranularity } = {}) =>
      request<Page<LogEntry> & { stats: LogStats }>(
        `/logs${logSearch({ ...params, stats: 1 })}`,
      ),

    get: (id: string) => request<LogEntry>(`/logs/${id}`),

    stats: (params: LogListParams & { granularity?: LogGranularity } = {}) =>
      request<LogStats>(`/logs/stats${logSearch(params)}`),

    clear: () => request<{ deleted: number }>('/logs', { method: 'DELETE' }),
  },

  files: {
    /**
     * File read token, valid for two minutes.
     *
     * An `<img>` tag and a navigation don't carry an `Authorization` header: it's this token, and
     * only this token, that authorizes previewing a protected file and downloading an archive.
     */
    token: async () => (await request<{ token: string }>('/files/token', { method: 'POST' })).token,
  },

  realtime: {
    /**
     * Declares the topics a stream follows.
     *
     * Replaces the list rather than extending it: switching screens must be able to unsubscribe
     * everything in a single request. This call is also what attaches the token to the stream,
     * which an `EventSource` cannot carry.
     */
    subscribe: (clientId: string, subscriptions: string[]) =>
      request<void>('/realtime', {
        method: 'POST',
        body: JSON.stringify({ clientId, subscriptions }),
      }),
  },

  storage: {
    /** Active store and usage. */
    get: () => request<Storage>('/storage'),

    objects: (params: StorageObjectsParams = {}) =>
      request<Page<StoredObject> & { orphans: number }>(
        `/storage/objects${toSearch({
          page: params.page,
          perPage: params.perPage,
          collection: params.collection,
          q: params.q,
          kind: params.kind,
          orphans: params.orphans === true ? 1 : undefined,
        })}`,
      ),

    remove: (keys: string[]) =>
      request<{ deleted: number }>('/storage/objects', {
        method: 'DELETE',
        body: JSON.stringify({ keys }),
      }),

    /** Exercises the store end to end: write, read back, signed URL, delete. */
    check: () => request<StorageProbe>('/storage/check', { method: 'POST' }),

    /**
     * Archive URL, carrying a short-lived token.
     *
     * A URL handed to the browser, not a `fetch` call: only the browser knows how to stream the
     * download to disk incrementally and resume an interrupted download. Pulling it into memory to
     * turn it into a downloadable object would try to fit an entire archive inside the tab.
     *
     * Hence the token as a parameter: a navigation doesn't carry an `Authorization` header. It's
     * the same mechanism as protected files, with the same two-minute lifetime.
     */
    archive: async (options: { collection?: string; thumbs?: boolean } = {}) => {
      const token = await api.files.token()

      return `/api/storage/archive${toSearch({
        collection: options.collection,
        thumbs: options.thumbs === true ? 1 : undefined,
        token,
      })}`
    },
  },

  auth: {
    /** Collection holding the superusers. */
    superusers: '_superusers',

    /** Collection holding the roles and their permissions. */
    roles: '_roles',

    login: async (identity: string, password: string) => {
      const result = await request<{ token: string; record: RecordValue }>(
        `/collections/${api.auth.superusers}/auth-with-password`,
        { method: 'POST', body: JSON.stringify({ identity, password }) },
      )

      session.token = result.token
      return result.record
    },

    me: () => request<Identity>('/me'),

    logout: async () => {
      try {
        await request<void>(`/collections/${api.auth.superusers}/auth-logout`, { method: 'POST' })
      } finally {
        // The local token is cleared even if the call fails: otherwise a network outage would
        // leave the user believing they're signed out while a token is still active in their tab.
        session.token = null
      }
    },

    /** Sets the roles and permissions of an account. Reserved for superusers. */
    grant: (collection: string, id: string, roles: string[], permissions: string[]) =>
      request<void>(`/collections/${collection}/records/${id}/grants`, {
        method: 'POST',
        body: JSON.stringify({ roles, permissions }),
      }),
  },

  collections: {
    list: () => request<{ items: Collection[] }>('/collections'),

    get: (nameOrId: string) => request<Collection>(`/collections/${nameOrId}`),

    create: (payload: CollectionPayload) =>
      request<Collection>('/collections', { method: 'POST', body: JSON.stringify(payload) }),

    update: (nameOrId: string, payload: CollectionPayload) =>
      request<Collection>(`/collections/${nameOrId}`, {
        method: 'PATCH',
        body: JSON.stringify(payload),
      }),

    remove: (name: string) => request<void>(`/collections/${name}`, { method: 'DELETE' }),
  },

  records: {
    list: (collection: string, params: RecordListParams = {}) =>
      request<Page<RecordValue>>(`/collections/${collection}/records${toQuery(params)}`),

    get: (collection: string, id: string) =>
      request<RecordValue>(`/collections/${collection}/records/${id}`),

    create: (collection: string, data: RecordValue) =>
      request<RecordValue>(`/collections/${collection}/records`, {
        method: 'POST',
        body: JSON.stringify(data),
      }),

    update: (collection: string, id: string, data: RecordValue) =>
      request<RecordValue>(`/collections/${collection}/records/${id}`, {
        method: 'PATCH',
        body: JSON.stringify(data),
      }),

    remove: (collection: string, id: string) =>
      request<void>(`/collections/${collection}/records/${id}`, { method: 'DELETE' }),
  },
}
