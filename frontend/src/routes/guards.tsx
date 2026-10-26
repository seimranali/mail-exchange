import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '@/app/AuthProvider'
import { LoadingState } from '@/components/data-display/States'
import { EmptyState } from '@/components/data-display/States'
import type { Role } from '@/types/api'

export function RequireAuth() {
  const { status } = useAuth()
  const location = useLocation()
  if (status === 'loading') return <LoadingState label="Restoring session" />
  if (status === 'anonymous') return <Navigate to="/login" replace state={{ from: location.pathname }} />
  return <Outlet />
}

/**
 * Pages that manage one organization's data (domains, mailboxes, compliance). A platform
 * administrator must first pick the organization in the top bar.
 */
export function RequireOrganizationScope({ children }: { children: ReactNode }) {
  const { hasOrganizationScope } = useAuth()
  if (!hasOrganizationScope) {
    return (
      <EmptyState
        icon="organization"
        title="Choose an organization"
        description="You are signed in as the platform administrator. Select the organization you want to manage in the top bar (or create one under Organizations), then return here."
      />
    )
  }
  return <>{children}</>
}

/**
 * Hides routes the user's roles cannot use. This is a UX convenience only:
 * the API enforces every permission independently.
 */
export function RequireRole({ roles, children }: { roles: Role[]; children: ReactNode }) {
  const { hasRole } = useAuth()
  if (!hasRole(...roles)) {
    return <EmptyState icon="lock" title="Access restricted" description="Your role does not include access to this area. Contact an organization administrator if you need it." />
  }
  return <>{children}</>
}
