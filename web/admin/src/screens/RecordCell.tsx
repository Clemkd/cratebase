import type { Field, RecordValue } from '../api'
import { asGeoPoint, asStringList } from '../lib/fields'
import { formatDateTime } from '../lib/format'
import { Badge, cn } from '../ui'

const EMPTY = <span className="text-ink-faint">—</span>

function BadgeList({ values, mono }: { values: string[]; mono?: boolean }) {
  if (values.length === 0) return EMPTY

  const shown = values.slice(0, 3)

  return (
    <span className="flex flex-wrap items-center gap-1">
      {shown.map((value) => (
        <Badge key={value} mono={mono} title={value}>
          {value}
        </Badge>
      ))}
      {values.length > shown.length && (
        <span className="text-xs text-ink-muted">+{values.length - shown.length}</span>
      )}
    </span>
  )
}

/** Renders a record value according to its column's logical type. */
export function RecordCell({ field, record }: { field: Field; record: RecordValue }) {
  const value = record[field.name]

  if (field.multiple) {
    return <BadgeList values={asStringList(value)} mono={field.type === 'Relation'} />
  }

  switch (field.type) {
    case 'Bool':
      return (
        <Badge tone={value === true ? 'success' : 'neutral'} dot>
          {value === true ? 'yes' : 'no'}
        </Badge>
      )

    case 'Date':
    case 'AutoDate': {
      const text = formatDateTime(value)

      return text === '' ? EMPTY : (
        <time
          dateTime={typeof value === 'string' ? value : undefined}
          title={typeof value === 'string' ? value : undefined}
          className="tabular-nums whitespace-nowrap text-ink-muted"
        >
          {text}
        </time>
      )
    }

    case 'Number':
      return (
        <span className="tabular-nums">
          {typeof value === 'number' ? value.toLocaleString('en-US') : String(value ?? '')}
        </span>
      )

    case 'Relation': {
      const id = String(value ?? '')

      return id === '' ? EMPTY : (
        <Badge mono title={id}>
          {id.slice(0, 8)}
        </Badge>
      )
    }

    case 'Select': {
      const text = String(value ?? '')

      return text === '' ? EMPTY : <Badge tone="brand">{text}</Badge>
    }

    case 'Json': {
      if (value === null || value === undefined) return EMPTY

      const text = JSON.stringify(value)

      return (
        <code
          title={text}
          className="block max-w-64 truncate rounded bg-surface-sunken px-1.5 py-0.5 font-mono text-xs text-ink-muted"
        >
          {text}
        </code>
      )
    }

    case 'GeoPoint': {
      const point = asGeoPoint(value)

      return (
        <span className="tabular-nums whitespace-nowrap text-ink-muted">
          {point.latitude.toFixed(4)}, {point.longitude.toFixed(4)}
        </span>
      )
    }

    case 'Url': {
      const href = String(value ?? '')

      return href === '' ? EMPTY : (
        <a
          href={href}
          target="_blank"
          rel="noreferrer noopener"
          className="text-brand underline-offset-4 hover:underline"
        >
          {href}
        </a>
      )
    }

    case 'Editor': {
      const text = String(value ?? '').replace(/<[^>]*>/g, ' ').trim()

      return text === '' ? EMPTY : (
        <span className="block max-w-80 truncate text-ink-muted" title={text}>
          {text}
        </span>
      )
    }

    default: {
      const text = String(value ?? '')

      if (text === '') return EMPTY

      return (
        <span
          title={text}
          className={cn('block max-w-72 truncate', field.name === 'id' && 'font-mono text-xs')}
        >
          {text}
        </span>
      )
    }
  }
}
