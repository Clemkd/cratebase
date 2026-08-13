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

export const api = {
  health: () => request<Health>('/health'),

  auth: {
    /** Collection portant les superadministrateurs. */
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

    /** Pose les rôles et permissions d'un compte. Réservé au superadministrateur. */
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
