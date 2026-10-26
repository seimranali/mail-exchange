import type { ReactNode } from 'react'
import { Button } from '../common/Button'
import styles from './display.module.css'

interface FilterPanelProps {
  children: ReactNode
  activeCount: number
  onClear: () => void
  label?: string
}

/** Groups filter controls with an accessible label and a "clear all" affordance. */
export function FilterPanel({ children, activeCount, onClear, label = 'Filters' }: FilterPanelProps) {
  return (
    <div role="group" aria-label={label} className={styles.filterPanel}>
      {children}
      {activeCount > 0 && (
        <Button variant="ghost" size="sm" icon="close" onClick={onClear}>
          Clear filters ({activeCount})
        </Button>
      )}
    </div>
  )
}
