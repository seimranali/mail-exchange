import { useQuery } from '@tanstack/react-query'
import { useAuth } from '@/app/AuthProvider'
import { organizationsApi } from '@/services/endpoints'
import { Select } from '../forms/Select'
import styles from './navigation.module.css'

const PLATFORM = ''

/** Lets a platform administrator choose which organization they are managing. */
export function OrganizationSwitcher() {
  const { activeOrganizationId, setActiveOrganization } = useAuth()
  const orgs = useQuery({
    queryKey: ['organizations', 'switcher'],
    queryFn: () => organizationsApi.listAll(),
    staleTime: 60_000,
  })

  return (
    <div className={styles.orgSwitcher}>
      <Select
        inputSize="sm"
        aria-label="Organization to manage"
        value={activeOrganizationId ?? PLATFORM}
        onChange={(e) => setActiveOrganization(e.target.value || null)}
        options={[
          { value: PLATFORM, label: 'All organizations (platform)' },
          ...(orgs.data?.items ?? []).map((o) => ({ value: o.id, label: o.name })),
        ]}
      />
    </div>
  )
}
