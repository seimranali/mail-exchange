import type { IconName } from '@/components/common/Icon'
import type { Role } from '@/types/api'

export interface NavItem {
  to: string
  label: string
  icon: IconName
  /** Visible to users holding any of these roles. Omit for everyone. */
  roles?: Role[]
}

export interface NavSection {
  label: string
  items: NavItem[]
}

const mail: Role[] = ['PlatformAdmin', 'OrgAdmin', 'MailAdmin']
const security: Role[] = ['PlatformAdmin', 'OrgAdmin', 'SecurityAdmin', 'Auditor']
const compliance: Role[] = ['PlatformAdmin', 'OrgAdmin', 'ComplianceOfficer', 'Auditor', 'SecurityAdmin']

export const navigation: NavSection[] = [
  { label: 'Overview', items: [{ to: '/', label: 'Dashboard', icon: 'dashboard' }] },
  {
    label: 'Mail',
    items: [
      { to: '/mailboxes', label: 'Mailboxes', icon: 'mailbox' },
      { to: '/domains', label: 'Domains', icon: 'domain' },
      { to: '/mail-queue', label: 'Mail queue', icon: 'queue', roles: mail },
    ],
  },
  {
    label: 'Protection',
    items: [
      { to: '/security', label: 'Security', icon: 'shield', roles: security },
      { to: '/quarantine', label: 'Quarantine', icon: 'quarantine', roles: security },
      { to: '/compliance', label: 'Compliance', icon: 'compliance', roles: compliance },
      { to: '/audit', label: 'Audit log', icon: 'audit', roles: compliance },
    ],
  },
  {
    label: 'Administration',
    items: [
      { to: '/organizations', label: 'Organizations', icon: 'organization', roles: ['PlatformAdmin', 'OrgAdmin'] },
      { to: '/settings', label: 'Settings', icon: 'settings' },
    ],
  },
]
