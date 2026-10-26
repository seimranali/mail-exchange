import { Icon } from '../common/Icon'
import styles from './DataTable.module.css'

export interface SortState {
  key: string
  direction: 'asc' | 'desc'
}

interface SortableHeaderProps {
  label: string
  sortKey: string
  sort?: SortState
  onSortChange: (sort: SortState) => void
}

/** Header button; the enclosing <th> carries aria-sort. */
export function SortableHeader({ label, sortKey, sort, onSortChange }: SortableHeaderProps) {
  const active = sort?.key === sortKey
  const next: SortState = { key: sortKey, direction: active && sort?.direction === 'asc' ? 'desc' : 'asc' }
  return (
    <button type="button" className={styles.sortButton} onClick={() => onSortChange(next)}>
      {label}
      <Icon name={active ? (sort?.direction === 'asc' ? 'chevronUp' : 'chevronDown') : 'sort'} size={14} className={active ? styles.sortActive : styles.sortIdle} />
      <span className="mx-visually-hidden">, sort {next.direction === 'asc' ? 'ascending' : 'descending'}</span>
    </button>
  )
}
