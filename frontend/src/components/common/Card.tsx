import type { HTMLAttributes, ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './common.module.css'

interface CardProps extends Omit<HTMLAttributes<HTMLElement>, 'title'> {
  title?: ReactNode
  description?: ReactNode
  actions?: ReactNode
  footer?: ReactNode
  padded?: boolean
  as?: 'section' | 'article' | 'div'
}

export function Card({ title, description, actions, footer, padded = true, as: Tag = 'section', className, children, ...rest }: CardProps) {
  const headingId = rest.id ? `${rest.id}-title` : undefined
  return (
    <Tag className={cx(styles.card, className)} aria-labelledby={title && headingId ? headingId : undefined} {...rest}>
      {(title || actions) && (
        <header className={styles.cardHeader}>
          <div className={styles.cardHeading}>
            {title && <h2 id={headingId} className={styles.cardTitle}>{title}</h2>}
            {description && <p className={styles.cardDescription}>{description}</p>}
          </div>
          {actions && <div className={styles.cardActions}>{actions}</div>}
        </header>
      )}
      <div className={cx(padded && styles.cardBody)}>{children}</div>
      {footer && <footer className={styles.cardFooter}>{footer}</footer>}
    </Tag>
  )
}
