/**
 * Client HTTP de la console.
 *
 * Ancêtre de `@cratebase/client`, le SDK public : même forme d'erreur, même conventions. Ce qui est
 * ici finira extrait, donc rien de propre à la console ne doit s'y glisser.
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

  /** Premier message d'erreur de validation, pour un affichage compact. */
  get firstValidationMessage(): string | undefined {
    if (!this.errors) return undefined
    const [field, messages] = Object.entries(this.errors)[0] ?? []
    return field && messages?.[0] ? `${field} : ${messages[0]}` : undefined
  }
}

/** Message lisible d'un échec, quelle qu'en soit la nature. */
export function describeFailure(failure: unknown): string {
  if (failure instanceof ApiError) {
    return failure.firstValidationMessage ?? failure.detail
  }

  if (failure instanceof Error) {
    return failure.message
  }

  return String(failure)
}

/** Erreurs de validation portées par un échec, indexées par champ. */
export function validationErrors(failure: unknown): Record<string, string[]> {
  return failure instanceof ApiError ? (failure.errors ?? {}) : {}
}

/**
 * Session courante.
 *
 * `sessionStorage` et non `localStorage` : le jeton disparaît à la fermeture de l'onglet, ce qui
 * limite la fenêtre d'exploitation sur un poste partagé. Le jeton étant révocable côté serveur, la
 * déconnexion le détruit vraiment — ici comme en base.
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
   * S'abonne à l'expiration du jeton.
   *
   * Un 401 peut survenir sur n'importe quel appel — jeton révoqué depuis un autre onglet, droits
   * retirés. Le traiter dans chaque écran laisserait forcément un chemin oublié, donc la purge et
   * le retour à la connexion sont déclenchés ici, une seule fois, pour tout le monde.
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
    // Un 401 sur un appel porteur d'un jeton signifie que ce jeton ne vaut plus rien : on le purge
    // avant de propager, sinon l'écran suivant le renverrait pour se faire refuser à son tour.
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
    problem.detail ?? problem.title ?? `Erreur HTTP ${status}`,
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

/** Charge utile d'un champ telle que le serveur l'attend. */
export interface FieldPayload {
  /**
   * ⚠️ Omis pour un champ nouveau, renvoyé tel quel pour un champ existant. C'est ce qui distingue
   * un renommage — qui préserve les données — d'un remplacement, qui les détruit.
   */
  id?: string
  name: string
  type: FieldType
  required: boolean
  maxSelect: number
  options: FieldOptions
}

/** Charge utile d'une collection telle que le serveur l'attend. */
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

/** Niveaux du plus bas au plus grave. L'ordre sert au filtre « au moins ce niveau ». */
export const LOG_LEVELS: LogLevel[] = ['Debug', 'Info', 'Warning', 'Error']

export interface LogEntry {
  id: string
  created: string
  level: LogLevel
  message: string
  method: string
  url: string
  /** Zéro pour une entrée d'application, qui ne répond à aucune requête. */
  status: number
  /** Durée de traitement, en millisecondes. */
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
   * Niveaux retenus. Vide ou absent : tous.
   *
   * Un ensemble et non une borne basse : « les avertissements sans les erreurs » est une question
   * d'exploitation courante, qu'une gravité minimale ne sait pas poser.
   */
  levels?: LogLevel[]
  /** Fragment cherché dans le message ou l'URL. */
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

/** Un objet du magasin, replacé dans le modèle de collections. */
export interface StoredObject {
  key: string
  collection: string
  recordId: string
  fileName: string
  size: number
  contentType: string
  isThumb: boolean
  /** Aucun enregistrement ne référence ce fichier. */
  orphan: boolean
}

export interface StorageObjectsParams {
  page?: number
  perPage?: number
  collection?: string
  q?: string
  /** `files`, `thumbs`, ou vide pour les deux. */
  kind?: string
  /** Ne montrer que les objets qu'aucun enregistrement ne référence. */
  orphans?: boolean
}

/**
 * Description du magasin de fichiers.
 *
 * Aucune clé secrète n'y figure : le magasin est décidé par la configuration de l'hôte, et la
 * console le lit sans jamais pouvoir l'écrire. `accessKeyHint` ne porte que les quatre derniers
 * caractères de la clé d'accès — assez pour reconnaître laquelle est en service.
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
 * Occupation mesurée de l'instance.
 *
 * Une capacité à zéro signifie « inconnue » et non « nulle » : ni PostgreSQL ni S3 n'exposent de
 * limite de façon portable, et l'écran doit alors montrer un volume sans jauge plutôt qu'une jauge
 * inventée.
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
 * Réglages soumis.
 *
 * Partiel de bout en bout : le serveur laisse en place ce que la charge ne mentionne pas. Envoyer
 * l'objet complet remettrait à leur valeur par défaut les réglages qu'un écran ne connaît pas.
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

/** Assemble une chaîne de requête en ignorant ce qui est vide. */
function toSearch(params: Record<string, string | number | undefined>): string {
  const entries = Object.entries(params)
    .filter((entry): entry is [string, string | number] => entry[1] !== undefined && entry[1] !== '')
    .map(([key, value]): [string, string] => [key, String(value)])

  const query = new URLSearchParams(entries).toString()

  return query ? `?${query}` : ''
}

/**
 * Chaîne de requête du journal.
 *
 * Les niveaux voyagent séparés par des virgules sous un seul `level` : `URLSearchParams` sait
 * répéter une clé, mais une adresse à quatre `level=` est illisible dans une barre de navigation —
 * or c'est là qu'on la relit quand on partage un lien de journal.
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

  /** État de l'instance : moteur, stockage, version, volumétrie. Réservé au super-admin. */
  instance: () => request<Instance>('/instance'),

  /**
   * Occupation du disque, de la base et des fichiers.
   *
   * Séparée de `/instance` parce qu'elle coûte réellement : elle interroge le moteur et parcourt le
   * magasin. Un écran qui ne veut que le nom de l'instance n'a pas à payer ce prix.
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
     * Page et histogramme en un seul appel.
     *
     * ⚠️ Indissociables volontairement : deux appels distincts vident chacun le tampon d'écriture
     * du serveur, donc le second voit des entrées que le premier n'avait pas — et le graphique
     * annonce un total que le tableau sous lui ne montre pas.
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
     * Jeton de lecture de fichier, valable deux minutes.
     *
     * Une balise `<img>` et une navigation ne portent pas d'en-tête `Authorization` : c'est ce
     * jeton, et lui seul, qui autorise l'aperçu d'un fichier protégé et le téléchargement d'une
     * archive.
     */
    token: async () => (await request<{ token: string }>('/files/token', { method: 'POST' })).token,
  },

  realtime: {
    /**
     * Déclare les sujets suivis par un flux.
     *
     * Remplace la liste, ne l'étend pas : changer d'écran doit pouvoir tout désabonner en une
     * requête. C'est aussi cet appel qui attache le jeton au flux, qu'une `EventSource` ne peut
     * pas porter.
     */
    subscribe: (clientId: string, subscriptions: string[]) =>
      request<void>('/realtime', {
        method: 'POST',
        body: JSON.stringify({ clientId, subscriptions }),
      }),
  },

  storage: {
    /** Magasin actif et volumétrie. */
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

    /** Éprouve le magasin de bout en bout : écriture, relecture, URL signée, suppression. */
    check: () => request<StorageProbe>('/storage/check', { method: 'POST' }),

    /**
     * Adresse de l'archive, munie d'un jeton de courte durée.
     *
     * Une URL confiée au navigateur, et non un appel `fetch` : lui seul sait écrire le flux sur le
     * disque au fur et à mesure et reprendre un téléchargement interrompu. Le rapatrier en mémoire
     * pour en faire un objet téléchargeable ferait tenir une archive entière dans l'onglet.
     *
     * D'où le jeton en paramètre : une navigation ne porte pas d'en-tête `Authorization`. C'est le
     * même mécanisme que les fichiers protégés, avec la même durée de vie de deux minutes.
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
    /** Collection portant les super-admins. */
    superusers: '_superusers',

    /** Collection portant les rôles et leurs permissions. */
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
        // Le jeton local part même si l'appel échoue : sinon une coupure réseau laisserait
        // l'utilisateur persuadé d'être déconnecté avec un jeton encore actif dans son onglet.
        session.token = null
      }
    },

    /** Pose les rôles et permissions d'un compte. Réservé au super-admin. */
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
