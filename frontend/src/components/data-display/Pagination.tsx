import { IconButton } from '../common/Button'
import { Select } from '../forms/Select'
import styles from './DataTable.module.css'

interface PaginationProps {
  page: number
  pageSize: number
  totalCount: number
  onPageChange: (page: number) => void
  onPageSizeChange?: (size: number) => void
  pageSizes?: number[]
}

export function Pagination({ page, pageSize, totalCount, onPageChange, onPageSizeChange, pageSizes = [10, 25, 50, 100] }: PaginationProps) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))
  const from = totalCount === 0 ? 0 : (page - 1) * pageSize + 1
  const to = Math.min(page * pageSize, totalCount)

  return (
    <nav className={styles.pagination} aria-label="Pagination">
      <p className={styles.paginationSummary} aria-live="polite">
        {from.toLocaleString()}–{to.toLocaleString()} of {totalCount.toLocaleString()}
      </p>
      <div className={styles.paginationControls}>
        {onPageSizeChange && (
          <label className={styles.pageSize}>
            <span>Rows</span>
            <Select
              inputSize="sm"
              value={String(pageSize)}
              onChange={(e) => onPageSizeChange(Number(e.target.value))}
              options={pageSizes.map((s) => ({ value: String(s), label: String(s) }))}
              aria-label="Rows per page"
            />
          </label>
        )}
        <IconButton icon="chevronLeft" label="Previous page" size="sm" variant="default" disabled={page <= 1} onClick={() => onPageChange(page - 1)} />
        <span className={styles.pageIndicator}>
          Page {page} of {totalPages}
        </span>
        <IconButton icon="chevronRight" label="Next page" size="sm" variant="default" disabled={page >= totalPages} onClick={() => onPageChange(page + 1)} />
      </div>
    </nav>
  )
}
