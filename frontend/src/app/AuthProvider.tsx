import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { activeOrgStore, setUnauthorizedHandler, tokenStore } from '@/services/apiClient'
import { authApi } from '@/services/endpoints'
import type { CurrentUser, Role } from '@/types/api'

interface AuthState {
  user: CurrentUser | null
  status: 'loading' | 'authenticated' | 'anonymous'
  login: (email: string, password: string) => Promise<void>
  logout: () => void
  hasRole: (...roles: Role[]) => boolean
  /** Platform administrators only: the organization being managed (null = platform-wide view). */
  activeOrganizationId: string | null
  setActiveOrganization: (id: string | null) => void
  /** True when the user can create/edit tenant data right now (has an organization in scope). */
  hasOrganizationScope: boolean
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [status, setStatus] = useState<AuthState['status']>(tokenStore.get() ? 'loading' : 'anonymous')
  const [activeOrganizationId, setActiveOrganizationId] = useState<string | null>(activeOrgStore.get())

  const logout = useCallback(() => {
    tokenStore.set(null)
    activeOrgStore.set(null)
    setActiveOrganizationId(null)
    setUser(null)
    setStatus('anonymous')
    queryClient.clear() // never show one user's cached data to the next
  }, [queryClient])

  useEffect(() => {
    setUnauthorizedHandler(logout)
    return () => setUnauthorizedHandler(null)
  }, [logout])

  // Restore the session from a stored token.
  useEffect(() => {
    if (!tokenStore.get()) return
    authApi
      .me()
      .then((u) => {
        setUser(u)
        setStatus('authenticated')
      })
      .catch(logout)
  }, [logout])

  const login = useCallback(async (email: string, password: string) => {
    const res = await authApi.login(email, password)
    tokenStore.set(res.accessToken)
    activeOrgStore.set(null)
    setActiveOrganizationId(null)
    setUser(res.user)
    setStatus('authenticated')
  }, [])

  const hasRole = useCallback((...roles: Role[]) => !!user && roles.some((r) => user.roles.includes(r)), [user])

  const setActiveOrganization = useCallback((id: string | null) => {
    activeOrgStore.set(id)
    setActiveOrganizationId(id)
    // Data from the previous organization must never be shown under the new one.
    queryClient.clear()
  }, [queryClient])

  const isPlatformAdmin = !!user?.roles.includes('PlatformAdmin')
  const hasOrganizationScope = !!user && (!isPlatformAdmin || activeOrganizationId !== null)

  const value = useMemo(
    () => ({ user, status, login, logout, hasRole, activeOrganizationId: isPlatformAdmin ? activeOrganizationId : null, setActiveOrganization, hasOrganizationScope }),
    [user, status, login, logout, hasRole, isPlatformAdmin, activeOrganizationId, setActiveOrganization, hasOrganizationScope],
  )
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>')
  return ctx
}
