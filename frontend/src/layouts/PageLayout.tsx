import { useEffect, type CSSProperties, type ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { Breadcrumbs, type Crumb } from '@/components/navigation/Breadcrumbs'
import styles from './layouts.module.css'

interface PageHeaderProps {
  title: string
  description?: ReactNode
  actions?: ReactNode
  breadcrumbs?: Crumb[]
}

/** Page title block. Also sets document.title so each route has a unique, descriptive title (WCAG 2.4.2). */
export function PageHeader({ title, description, actions, breadcrumbs }: PageHeaderProps) {
  useEffect(() => {
    document.title = `${title} · MX Admin`
  }, [title])

  return (
    <header className={styles.pageHeader}>
      {breadcrumbs && <Breadcrumbs items={breadcrumbs} />}
      <div className={styles.pageHeaderRow}>
        <div className={styles.pageHeading}>
          <h1 className={styles.pageTitle}>{title}</h1>
          {description && <p className={styles.pageDescription}>{description}</p>}
        </div>
        {actions && <div className={styles.pageActions}>{actions}</div>}
      </div>
    </header>
  )
}

export function ContentContainer({ children, narrow }: { children: ReactNode; narrow?: boolean }) {
  return <div className={cx(styles.container, narrow && styles.narrow)}>{children}</div>
}

interface ResponsiveGridProps {
  children: ReactNode
  /** Minimum column width before wrapping. */
  minColumnWidth?: string
  gap?: 'sm' | 'md' | 'lg'
  className?: string
}

/** Auto-fitting grid: as many columns as fit at `minColumnWidth`, no breakpoints required. */
export function ResponsiveGrid({ children, minColumnWidth = '240px', gap = 'md', className }: ResponsiveGridProps) {
  return (
    <div className={cx(styles.grid, styles[`gap${gap.toUpperCase()}`], className)} style={{ '--grid-min': minColumnWidth } as CSSProperties}>
      {children}
    </div>
  )
}

interface SplitPanelProps {
  primary: ReactNode
  secondary: ReactNode
  /** Width of the secondary column on wide screens. */
  secondaryWidth?: string
}

/** Two-column layout that stacks below 1024px. */
export function SplitPanel({ primary, secondary, secondaryWidth = '360px' }: SplitPanelProps) {
  return (
    <div className={styles.split} style={{ '--split-secondary': secondaryWidth } as CSSProperties}>
      <div className={styles.splitPrimary}>{primary}</div>
      <div className={styles.splitSecondary}>{secondary}</div>
    </div>
  )
}

export function Stack({ children, gap = 'md' }: { children: ReactNode; gap?: 'sm' | 'md' | 'lg' }) {
  return <div className={cx(styles.stack, styles[`gap${gap.toUpperCase()}`])}>{children}</div>
}
