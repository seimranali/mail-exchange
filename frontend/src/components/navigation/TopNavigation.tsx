import type { ReactNode } from 'react'
import type { ThemePreference } from '@/hooks/useTheme'
import type { CurrentUser } from '@/types/api'
import { IconButton } from '../common/Button'
import { DropdownMenu } from '../overlays/DropdownMenu'
import styles from './navigation.module.css'

interface TopNavigationProps {
  user: CurrentUser
  theme: ThemePreference
  onThemeChange: (theme: ThemePreference) => void
  onLogout: () => void
  onToggleSidebar: () => void
  sidebarOpen: boolean
  notifications?: ReactNode
  /** Shown instead of the organization name (platform administrators). */
  organizationSwitcher?: ReactNode
}

export function TopNavigation({ user, theme, onThemeChange, onLogout, onToggleSidebar, sidebarOpen, notifications, organizationSwitcher }: TopNavigationProps) {
  const initials = user.displayName.split(/\s+/).map((p) => p[0]).slice(0, 2).join('').toUpperCase()
  return (
    <header className={styles.topbar}>
      <IconButton
        icon="menu"
        label={sidebarOpen ? 'Close navigation' : 'Open navigation'}
        className={styles.menuToggle}
        aria-expanded={sidebarOpen}
        aria-controls="mx-sidebar"
        onClick={onToggleSidebar}
      />
      {organizationSwitcher ?? <div className={styles.orgName} title={user.organizationName}>{user.organizationName}</div>}
      <div className={styles.topbarActions}>
        <DropdownMenu
          label={`Theme: ${theme}`}
          iconOnly
          icon={theme === 'dark' ? 'moon' : theme === 'light' ? 'sun' : 'monitor'}
          variant="ghost"
          size="md"
          items={(['system', 'light', 'dark'] as const).map((t) => ({
            id: t,
            label: t === 'system' ? 'Match system' : t === 'light' ? 'Light' : 'Dark',
            checked: theme === t,
            onSelect: () => onThemeChange(t),
          }))}
        />
        {notifications}
        <div className={styles.userMenu}>
          <span className={styles.avatar} aria-hidden="true">{initials}</span>
          <DropdownMenu
            label={user.displayName}
            variant="ghost"
            size="md"
            items={[
              { id: 'email', label: user.email, icon: 'user', onSelect: () => undefined, disabled: true },
              { id: 'logout', label: 'Sign out', icon: 'logout', onSelect: onLogout },
            ]}
          />
        </div>
      </div>
    </header>
  )
}
