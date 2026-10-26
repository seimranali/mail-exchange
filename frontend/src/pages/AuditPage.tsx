import { useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { FilterPanel } from '@/components/data-display/FilterPanel'
import { Pagination } from '@/components/data-display/Pagination'
import { Checkbox } from '@/components/forms/Choice'
import { SearchInput } from '@/components/forms/TextInput'
import { AuditEventTable } from '@/components/mail-administration/Operations'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { ContentContainer, PageHeader } from '@/layouts/PageLayout'
import { complianceApi } from '@/services/endpoints'
import styles from './pages.module.css'

export default function AuditPage() {
  const [action, setAction] = useState('')
  const [privilegedOnly, setPrivilegedOnly] = useState(false)
  const [page, setPage] = useState(1)
  const debounced = useDebouncedValue(action)

  const events = useQuery({
    queryKey: ['audit', debounced, privilegedOnly, page],
    queryFn: () => complianceApi.auditEvents({ action: debounced || undefined, privilegedOnly: privilegedOnly || undefined, page, pageSize: 50 }),
    placeholderData: keepPreviousData,
  })

  return (
    <ContentContainer>
      <PageHeader title="Audit log" description="Append-only record of administrative actions. Entries cannot be edited or deleted." />
      <AuditEventTable
        rows={events.data?.items}
        loading={events.isFetching}
        error={events.error}
        onRetry={() => events.refetch()}
        toolbar={
          <FilterPanel activeCount={(debounced ? 1 : 0) + (privilegedOnly ? 1 : 0)} onClear={() => { setAction(''); setPrivilegedOnly(false); setPage(1) }}>
            <SearchInput className={styles.toolbarSearch} inputSize="sm" value={action} onValueChange={(v) => { setAction(v); setPage(1) }} placeholder="Action prefix, e.g. mailbox." />
            <Checkbox label="Privileged only" checked={privilegedOnly} onChange={(e) => { setPrivilegedOnly(e.target.checked); setPage(1) }} />
          </FilterPanel>
        }
        footer={events.data && events.data.totalCount > 0 && <Pagination page={page} pageSize={50} totalCount={events.data.totalCount} onPageChange={setPage} />}
      />
    </ContentContainer>
  )
}
