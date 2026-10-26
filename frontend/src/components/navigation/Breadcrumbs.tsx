import { Link } from 'react-router-dom'
import { Icon } from '../common/Icon'
import styles from './navigation.module.css'

export interface Crumb {
  label: string
  to?: string
}

export function Breadcrumbs({ items }: { items: Crumb[] }) {
  return (
    <nav aria-label="Breadcrumb">
      <ol className={styles.breadcrumbs}>
        {items.map((c, i) => {
          const last = i === items.length - 1
          return (
            <li key={`${c.label}-${i}`} className={styles.crumb}>
              {c.to && !last ? <Link to={c.to}>{c.label}</Link> : <span aria-current={last ? 'page' : undefined}>{c.label}</span>}
              {!last && <Icon name="chevronRight" size={14} className={styles.crumbSep} />}
            </li>
          )
        })}
      </ol>
    </nav>
  )
}
