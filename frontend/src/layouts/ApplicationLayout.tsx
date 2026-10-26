import { useEffect, useState } from 'react'
import { Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '@/app/AuthProvider'
import { NotificationCenter, type AppNotification } from '@/components/overlays/NotificationCenter'
import { OrganizationSwitcher } from '@/components/navigation/OrganizationSwitcher'
import { Sidebar } from '@/components/navigation/Sidebar'
import { TopNavigation } from '@/components/navigation/TopNavigation'
import { useTheme } from '@/hooks/useTheme'
import { navigation } from '@/routes/navigation'
import { useRealtime } from '@/services/realtime'
import { useQuery } from '@tanstack/react-query'
import { securityApi } from '@/services/endpoints'
import styles from './layouts.module.css'

export function ApplicationLayout() {
  const { user, hasRole, logout } = useAuth()
  const { theme, setTheme } = useTheme()
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [readIds, setReadIds] = useState<Set<string>>(new Set())
  const location = useLocation()
  useRealtime(Boolean(user))

  // Move focus to the main region after navigation so screen reader users land on new content.
  useEffect(() => {
    document.getElementById('mx-main')?.focus({ preventScroll: true })
    setSidebarOpen(false)
  }, [location.pathname])

  useEffect(() => {
    if (!sidebarOpen) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setSidebarOpen(false)
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [sidebarOpen])

  // Open security alerts surface as notifications for roles that can act on them.
  const canSeeAlerts = hasRole('PlatformAdmin', 'OrgAdmin', 'SecurityAdmin', 'Auditor')
  const alerts = useQuery({
    queryKey: ['security', 'alerts', 'open-notifications'],
    queryFn: () => securityApi.alerts({ status: 'Open', pageSize: 10 }),
    enabled: canSeeAlerts,
    refetchInterval: 60_000,
  })
  const notifications: AppNotification[] = (alerts.data?.items ?? []).map((a) => ({
    id: a.id,
    title: a.title,
    message: `${a.severity} · ${a.category}`,
    level: a.severity === 'Critical' || a.severity === 'High' ? 'Error' : 'Warning',
    link: '/security',
    createdAt: a.detectedAt,
    read: readIds.has(a.id),
  }))

  if (!user) return null

  return (
    <div className={styles.app}>
      <a href="#mx-main" className="mx-skip-link">Skip to main content</a>
      <Sidebar sections={navigation} hasRole={hasRole} open={sidebarOpen} onNavigate={() => setSidebarOpen(false)} />
      {sidebarOpen && <div className={styles.scrim} onClick={() => setSidebarOpen(false)} aria-hidden="true" />}
      <div className={styles.main}>
        <TopNavigation
          user={user}
          theme={theme}
          onThemeChange={setTheme}
          onLogout={logout}
          sidebarOpen={sidebarOpen}
          onToggleSidebar={() => setSidebarOpen((o) => !o)}
          organizationSwitcher={hasRole('PlatformAdmin') ? <OrganizationSwitcher /> : undefined}
          notifications={
            canSeeAlerts ? (
              <NotificationCenter
                notifications={notifications}
                onMarkAllRead={() => setReadIds(new Set(notifications.map((n) => n.id)))}
                onOpenItem={(id) => setReadIds((s) => new Set(s).add(id))}
              />
            ) : undefined
          }
        />
        <main id="mx-main" tabIndex={-1} className={styles.content}>
          <Outlet />
        </main>
      </div>
    </div>
  )
}
