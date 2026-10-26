import { useMemo, type KeyboardEvent, type ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { EmptyState, ErrorState, LoadingState } from './States'
import { SortableHeader, type SortState } from './SortableHeader'
import styles from './DataTable.module.css'

export interface Column<T> {
  id: string
  header: string
  cell: (row: T) => ReactNode
  /** Server-side sort key; makes the header sortable when set. */
  sortKey?: string
  align?: 'start' | 'end' | 'center'
  width?: string
  /** Can the user hide this column via ColumnSelector? */
  hideable?: boolean
  /** Hide below 768px to keep small screens readable. */
  hideOnMobile?: boolean
}

interface DataTableProps<T> {
  caption: string
  columns: Column<T>[]
  rows: T[] | undefined
  rowKey: (row: T) => string
  loading?: boolean
  error?: unknown
  onRetry?: () => void
  sort?: SortState
  onSortChange?: (sort: SortState) => void
  hiddenColumns?: string[]
  onRowClick?: (row: T) => void
  rowLabel?: (row: T) => string
  empty?: ReactNode
  toolbar?: ReactNode
  footer?: ReactNode
  /** Show the caption visually (default: screen-reader only). */
  showCaption?: boolean
}

export function DataTable<T>({
  caption, columns, rows, rowKey, loading, error, onRetry, sort, onSortChange, hiddenColumns = [], onRowClick, rowLabel,
  empty, toolbar, footer, showCaption,
}: DataTableProps<T>) {
  const visible = useMemo(() => columns.filter((c) => !hiddenColumns.includes(c.id)), [columns, hiddenColumns])

  const onRowKeyDown = (e: KeyboardEvent<HTMLTableRowElement>, row: T) => {
    if (onRowClick && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault()
      onRowClick(row)
    }
  }

  let body: ReactNode
  if (error) {
    body = <ErrorState error={error} onRetry={onRetry} />
  } else if (loading && !rows) {
    body = <LoadingState label={`Loading ${caption.toLowerCase()}`} />
  } else if (!rows || rows.length === 0) {
    body = empty ?? <EmptyState title="Nothing to show" description="No records match the current filters." />
  }

  return (
    <div className={styles.wrapper}>
      {toolbar && <div className={styles.toolbar}>{toolbar}</div>}
      {body ? (
        <div className={styles.stateBody}>{body}</div>
      ) : (
        <div className={styles.scroll} tabIndex={0} role="region" aria-label={caption}>
          <table className={cx(styles.table, loading && styles.refreshing)} aria-busy={loading || undefined}>
            <caption className={showCaption ? styles.caption : 'mx-visually-hidden'}>{caption}</caption>
            <thead>
              <tr>
                {visible.map((c) => (
                  <th
                    key={c.id}
                    scope="col"
                    style={{ width: c.width }}
                    className={cx(styles[c.align ?? 'start'], c.hideOnMobile && styles.hideMobile)}
                    aria-sort={c.sortKey && sort?.key === c.sortKey ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined}
                  >
                    {c.sortKey && onSortChange ? (
                      <SortableHeader label={c.header} sortKey={c.sortKey} sort={sort} onSortChange={onSortChange} />
                    ) : (
                      c.header
                    )}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows!.map((row) => (
                <tr
                  key={rowKey(row)}
                  className={cx(onRowClick && styles.clickable)}
                  tabIndex={onRowClick ? 0 : undefined}
                  aria-label={onRowClick && rowLabel ? rowLabel(row) : undefined}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  onKeyDown={(e) => onRowKeyDown(e, row)}
                >
                  {visible.map((c) => (
                    <td key={c.id} className={cx(styles[c.align ?? 'start'], c.hideOnMobile && styles.hideMobile)}>
                      {c.cell(row)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {footer && <div className={styles.footer}>{footer}</div>}
    </div>
  )
}
