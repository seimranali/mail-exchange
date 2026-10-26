import { lazy, Suspense, type ReactNode } from 'react'
import { createBrowserRouter } from 'react-router-dom'
import { LoadingState } from '@/components/data-display/States'
import { ApplicationLayout } from '@/layouts/ApplicationLayout'
import { LoginPage } from '@/pages/LoginPage'
import { NotFoundPage } from '@/pages/NotFoundPage'
import type { Role } from '@/types/api'
import { RequireAuth, RequireOrganizationScope, RequireRole } from './guards'

// Route-level code splitting keeps the initial bundle small.
const DashboardPage = lazy(() => import('@/pages/DashboardPage'))
const MailboxesPage = lazy(() => import('@/pages/MailboxesPage'))
const DomainsPage = lazy(() => import('@/pages/DomainsPage'))
const MailQueuePage = lazy(() => import('@/pages/MailQueuePage'))
const SecurityPage = lazy(() => import('@/pages/SecurityPage'))
const QuarantinePage = lazy(() => import('@/pages/QuarantinePage'))
const CompliancePage = lazy(() => import('@/pages/CompliancePage'))
const AuditPage = lazy(() => import('@/pages/AuditPage'))
const OrganizationsPage = lazy(() => import('@/pages/OrganizationsPage'))
const SettingsPage = lazy(() => import('@/pages/SettingsPage'))
const WebmailLoginPage = lazy(() => import('@/pages/webmail/WebmailLoginPage'))
const WebmailApp = lazy(() => import('@/pages/webmail/WebmailApp'))

const page = (node: ReactNode, roles?: Role[]) => (
  <Suspense fallback={<LoadingState />}>{roles ? <RequireRole roles={roles}>{node}</RequireRole> : node}</Suspense>
)

const mail: Role[] = ['PlatformAdmin', 'OrgAdmin', 'MailAdmin']
const security: Role[] = ['PlatformAdmin', 'OrgAdmin', 'SecurityAdmin', 'Auditor']
const compliance: Role[] = ['PlatformAdmin', 'OrgAdmin', 'ComplianceOfficer', 'Auditor', 'SecurityAdmin']

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  // Webmail for mailbox users: its own sign-in and token, outside the admin layout and guards.
  { path: '/mail/login', element: <Suspense fallback={<LoadingState />}><WebmailLoginPage /></Suspense> },
  { path: '/mail/:folder?', element: <Suspense fallback={<LoadingState />}><WebmailApp /></Suspense> },
  {
    element: <RequireAuth />,
    children: [
      {
        element: <ApplicationLayout />,
        children: [
          { index: true, element: page(<DashboardPage />) },
          { path: 'mailboxes', element: page(<RequireOrganizationScope><MailboxesPage /></RequireOrganizationScope>) },
          { path: 'domains', element: page(<RequireOrganizationScope><DomainsPage /></RequireOrganizationScope>) },
          { path: 'mail-queue', element: page(<MailQueuePage />, mail) },
          { path: 'security', element: page(<SecurityPage />, security) },
          { path: 'quarantine', element: page(<QuarantinePage />, security) },
          { path: 'compliance', element: page(<RequireOrganizationScope><CompliancePage /></RequireOrganizationScope>, compliance) },
          { path: 'audit', element: page(<AuditPage />, compliance) },
          { path: 'organizations', element: page(<OrganizationsPage />, ['PlatformAdmin', 'OrgAdmin']) },
          { path: 'settings', element: page(<SettingsPage />) },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
])
