import { useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useAuth } from '@/app/AuthProvider'
import { DataTable, type Column } from '@/components/data-display/DataTable'
import { FilterPanel } from '@/components/data-display/FilterPanel'
import { Pagination } from '@/components/data-display/Pagination'
import { EmptyState, ErrorState, LoadingState } from '@/components/data-display/States'
import { StatusBadge } from '@/components/data-display/StatusBadge'
import { Select } from '@/components/forms/Select'
import { SecurityAlertCard } from '@/components/mail-administration/Operations'
import { useApiMutation } from '@/hooks/useApiMutation'
import { ContentContainer, PageHeader } from '@/layouts/PageLayout'
import { formatDateTime } from '@/lib/format'
import { securityApi } from '@/services/endpoints'
import type { AlertSeverity, AlertStatus, AuthenticationEvent } from '@/types/api'
import styles from './pages.module.css'

export default function SecurityPage() {
  const { hasRole } = useAuth()
  const canManage = hasRole('PlatformAdmin', 'OrgAdmin', 'SecurityAdmin')
  const [status, setStatus] = useState<AlertStatus | ''>('Open')
  const [severity, setSeverity] = useState<AlertSeverity | ''>('')
  const [signInPage, setSignInPage] = useState(1)

  const alerts = useQuery({
    queryKey: ['security', 'alerts', status, severity],
    queryFn: () => securityApi.alerts({ status: status || undefined, severity: severity || undefined, pageSize: 50 }),
  })
  const signIns = useQuery({
    queryKey: ['security', 'sign-ins', signInPage],
    queryFn: () => securityApi.signIns({ page: signInPage, pageSize: 10 }),
    placeholderData: keepPreviousData,
  })
  const setAlertStatus = useApiMutation({
    mutationFn: ({ id, status }: { id: string; status: AlertStatus }) => securityApi.setAlertStatus(id, status),
    invalidate: [['security'], ['dashboard']],
    successMessage: (_, v) => `Alert ${v.status.toLowerCase()}`,
    errorTitle: 'Could not update alert',
  })

  const signInColumns: Column<AuthenticationEvent>[] = [
    { id: 'time', header: 'Time', cell: (e) => formatDateTime(e.occurredAt) },
    { id: 'email', header: 'Account', cell: (e) => e.attemptedEmail },
    { id: 'outcome', header: 'Outcome', cell: (e) => <StatusBadge status={e.outcome} /> },
    { id: 'ip', header: 'IP address', cell: (e) => e.ipAddress ?? '—', hideOnMobile: true },
  ]

  return (
    <ContentContainer>
      <PageHeader title="Security" description="Threat alerts and sign-in activity for your organization." />
      <section className={styles.section} aria-labelledby="alerts-heading">
        <div className={styles.inlineGroup} style={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <h2 id="alerts-heading" className={styles.sectionTitle}>Alerts</h2>
          <FilterPanel activeCount={(status ? 1 : 0) + (severity ? 1 : 0)} onClear={() => { setStatus(''); setSeverity('') }} label="Alert filters">
            <Select inputSize="sm" className={styles.toolbarFilter} aria-label="Filter by status" placeholder="All statuses" value={status}
              onChange={(e) => setStatus(e.target.value as AlertStatus | '')}
              options={(['Open', 'Acknowledged', 'Resolved', 'Dismissed'] as const).map((s) => ({ value: s, label: s }))} />
            <Select inputSize="sm" className={styles.toolbarFilter} aria-label="Filter by severity" placeholder="All severities" value={severity}
              onChange={(e) => setSeverity(e.target.value as AlertSeverity | '')}
              options={(['Critical', 'High', 'Medium', 'Low'] as const).map((s) => ({ value: s, label: s }))} />
          </FilterPanel>
        </div>
        {alerts.isLoading && <LoadingState label="Loading alerts" />}
        {alerts.error && <ErrorState error={alerts.error} onRetry={() => alerts.refetch()} />}
        {alerts.data?.items.length === 0 && <EmptyState icon="shield" title="No alerts" description="Nothing matches these filters." />}
        <div className={styles.list}>
          {alerts.data?.items.map((a) => (
            <SecurityAlertCard key={a.id} alert={a}
              onAcknowledge={canManage ? () => setAlertStatus.mutate({ id: a.id, status: 'Acknowledged' }) : undefined}
              onResolve={canManage ? () => setAlertStatus.mutate({ id: a.id, status: 'Resolved' }) : undefined}
              onDismiss={canManage ? () => setAlertStatus.mutate({ id: a.id, status: 'Dismissed' }) : undefined} />
          ))}
        </div>
      </section>

      <section className={styles.section} aria-labelledby="signins-heading">
        <h2 id="signins-heading" className={styles.sectionTitle}>Recent sign-ins</h2>
        <DataTable caption="Sign-in events" columns={signInColumns} rows={signIns.data?.items} rowKey={(e) => e.id}
          loading={signIns.isFetching} error={signIns.error} onRetry={() => signIns.refetch()}
          footer={signIns.data && signIns.data.totalCount > 0 && (
            <Pagination page={signInPage} pageSize={10} totalCount={signIns.data.totalCount} onPageChange={setSignInPage} />
          )} />
      </section>
    </ContentContainer>
  )
}
