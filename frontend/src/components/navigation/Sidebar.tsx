import { NavLink } from 'react-router-dom'
import { cx } from '@/lib/cx'
import type { NavSection } from '@/routes/navigation'
import type { Role } from '@/types/api'
import { Icon } from '../common/Icon'
import styles from './navigation.module.css'

interface SidebarProps {
  sections: NavSection[]
  hasRole: (...roles: Role[]) => boolean
  open: boolean
  onNavigate: () => void
}

export function Sidebar({ sections, hasRole, open, onNavigate }: SidebarProps) {
  return (
    <aside id="mx-sidebar" className={cx(styles.sidebar, open && styles.sidebarOpen)} aria-label="Main">
      <div className={styles.brand}>
        <img src="/mx-icon.svg" alt="" width={28} height={28} />
        <span className={styles.brandName}>MX Admin</span>
      </div>
      <nav className={styles.nav} aria-label="Primary">
        {sections.map((section) => {
          const items = section.items.filter((i) => !i.roles || hasRole(...i.roles))
          if (items.length === 0) return null
          return (
            <div key={section.label} className={styles.navSection}>
              <h2 className={styles.navSectionLabel}>{section.label}</h2>
              <ul className={styles.navList}>
                {items.map((item) => (
                  <li key={item.to}>
                    <NavLink
                      to={item.to}
                      end={item.to === '/'}
                      className={({ isActive }) => cx(styles.navLink, isActive && styles.navLinkActive)}
                      onClick={onNavigate}
                    >
                      <Icon name={item.icon} size={18} />
                      <span>{item.label}</span>
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          )
        })}
      </nav>
    </aside>
  )
}
