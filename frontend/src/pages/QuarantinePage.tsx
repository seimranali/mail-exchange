import { useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useAuth } from '@/app/AuthProvider'
import { FilterPanel } from '@/components/data-display/FilterPanel'
import { Pagination } from '@/components/data-display/Pagination'
import { Select } from '@/components/forms/Select'
import { SearchInput } from '@/components/forms/TextInput'
import { QuarantineTable } from '@/components/mail-administration/Operations'
import { ConfirmationDialog } from '@/components/overlays/ConfirmationDialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { ContentContainer, PageHeader } from '@/layouts/PageLayout'
import { securityApi } from '@/services/endpoints'
import type { QuarantineRecord, QuarantineStatus } from '@/types/api'
import styles from './pages.module.css'

export default function QuarantinePage() {
  const { hasRole } = useAuth()
  const canManage = hasRole('PlatformAdmin', 'OrgAdmin', 'SecurityAdmin')
  const [status, setStatus] = useState<QuarantineStatus | ''>('Held')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [pending, setPending] = useState<{ record: QuarantineRecord; action: 'release' | 'delete' } | null>(null)
  const debounced = useDebouncedValue(search)

  const list = useQuery({
    queryKey: ['security', 'quarantine', status, debounced, page],
    queryFn: () => securityApi.quarantine({ status: status || undefined, search: debounced, page, pageSize: 25 }),
    placeholderData: keepPreviousData,
  })
  const act = useApiMutation({
    mutationFn: ({ record, action }: { record: QuarantineRecord; action: 'release' | 'delete' }) =>
      action === 'release' ? securityApi.release(record.id) : securityApi.deleteQuarantine(record.id),
    invalidate: [['security', 'quarantine'], ['dashboard']],
    successMessage: (_, v) => (v.action === 'release' ? 'Message released to the recipient' : 'Message deleted'),
    errorTitle: 'Action failed',
    onSuccess: () => setPending(null),
  })

  return (
    <ContentContainer>
      <PageHeader title="Quarantine" description="Messages held by spam, malware, phishing and policy filters. Message content stays in the mail store." />
      <QuarantineTable
        rows={list.data?.items}
        loading={list.isFetching}
        error={list.error}
        onRetry={() => list.refetch()}
        onRelease={canManage ? (record) => setPending({ record, action: 'release' }) : undefined}
        onDelete={canManage ? (record) => setPending({ record, action: 'delete' }) : undefined}
        toolbar={
          <FilterPanel activeCount={(search ? 1 : 0) + (status ? 1 : 0)} onClear={() => { setSearch(''); setStatus(''); setPage(1) }}>
            <SearchInput className={styles.toolbarSearch} inputSize="sm" value={search} onValueChange={(v) => { setSearch(v); setPage(1) }} placeholder="Search sender, recipient or subject" />
            <Select className={styles.toolbarFilter} inputSize="sm" aria-label="Filter by status" placeholder="All statuses" value={status}
              onChange={(e) => { setStatus(e.target.value as QuarantineStatus | ''); setPage(1) }}
              options={(['Held', 'Released', 'Deleted'] as const).map((s) => ({ value: s, label: s }))} />
          </FilterPanel>
        }
        footer={list.data && list.data.totalCount > 0 && <Pagination page={page} pageSize={25} totalCount={list.data.totalCount} onPageChange={setPage} />}
      />
      <ConfirmationDialog
        open={!!pending}
        title={pending?.action === 'release' ? 'Release message?' : 'Delete message?'}
        message={pending?.action === 'release'
          ? <>The message from <strong>{pending.record.sender}</strong> will be delivered to <strong>{pending.record.recipient}</strong>. Only release messages you have confirmed are safe.</>
          : <>The message from <strong>{pending?.record.sender}</strong> will be permanently deleted.</>}
        confirmLabel={pending?.action === 'release' ? 'Release' : 'Delete'}
        destructive={pending?.action === 'delete'}
        loading={act.isPending}
        onConfirm={() => pending && act.mutate(pending)}
        onCancel={() => setPending(null)}
      />
    </ContentContainer>
  )
}
