import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Card } from '@/components/common/Card'
import { ActivityFeed, MetricCard, ProgressBar } from '@/components/data-display/Metrics'
import { ErrorState, LoadingState } from '@/components/data-display/States'
import { ServerHealthCard } from '@/components/mail-administration/Operations'
import { ContentContainer, PageHeader, ResponsiveGrid, SplitPanel } from '@/layouts/PageLayout'
import { formatBytes, formatNumber, formatRelative, humanize, percent } from '@/lib/format'
import { dashboardApi } from '@/services/endpoints'
import type { IconName } from '@/components/common/Icon'

const activityIcon = (entityType: string): IconName =>
  ({ Mailbox: 'mailbox', Domain: 'domain', LegalHold: 'compliance', SecurityPolicy: 'shield', QuarantineRecord: 'quarantine' } as Record<string, IconName>)[entityType] ?? 'audit'

export default function DashboardPage() {
  const { data, isLoading, error, refetch } = useQuery({ queryKey: ['dashboard'], queryFn: dashboardApi.get, refetchInterval: 60_000 })

  return (
    <ContentContainer>
      <PageHeader title="Dashboard" description="Health and activity across your mail environment." />
      {isLoading && <LoadingState label="Loading dashboard" />}
      {error && <ErrorState error={error} onRetry={() => refetch()} />}
      {data && (
        <>
          <ResponsiveGrid minColumnWidth="220px">
            <MetricCard label="Active mailboxes" icon="mailbox" value={formatNumber(data.activeMailboxes)}
              detail={data.mailboxLimit ? `${percent(data.activeMailboxes, data.mailboxLimit)}% of ${formatNumber(data.mailboxLimit)} licensed` : undefined} to="/mailboxes" />
            <MetricCard label="Domains" icon="domain" value={`${data.verifiedDomains}/${data.domains}`} detail="verified" tone={data.verifiedDomains < data.domains ? 'warning' : 'success'} to="/domains" />
            <MetricCard label="Open security alerts" icon="shield" value={data.openAlerts}
              detail={data.criticalAlerts ? `${data.criticalAlerts} critical` : 'None critical'} tone={data.criticalAlerts ? 'danger' : data.openAlerts ? 'warning' : 'success'} to="/security" />
            <MetricCard label="Held in quarantine" icon="quarantine" value={formatNumber(data.quarantineHeld)} tone="accent" to="/quarantine" />
          </ResponsiveGrid>

          <div style={{ marginTop: 'var(--mx-spacing-6)' }}>
            <SplitPanel
              primary={
                <>
                  <Card title="Mail servers" description="Latest health sample per server">
                    {data.servers.length === 0 ? (
                      <p>No mail servers are registered.</p>
                    ) : (
                      <ResponsiveGrid minColumnWidth="260px" gap="sm">
                        {data.servers.map((s) => <ServerHealthCard key={s.id} server={s} />)}
                      </ResponsiveGrid>
                    )}
                  </Card>
                  <Card title="Storage">
                    <ProgressBar label="Organization storage" value={data.storageUsedBytes} max={data.storageLimitBytes || 1} showValue
                      valueText={`${formatBytes(data.storageUsedBytes)} of ${formatBytes(data.storageLimitBytes)}`} />
                  </Card>
                </>
              }
              secondary={
                <Card title="Recent activity" actions={<Link to="/audit">View audit log</Link>}>
                  {data.recentActivity.length === 0 ? (
                    <p>No recent activity.</p>
                  ) : (
                    <ActivityFeed
                      label="Recent activity"
                      items={data.recentActivity.map((a) => ({
                        id: a.id,
                        icon: activityIcon(a.entityType),
                        text: humanize(a.action.replace('.', ' ')),
                        time: formatRelative(a.occurredAt),
                      }))}
                    />
                  )}
                </Card>
              }
            />
          </div>
        </>
      )}
    </ContentContainer>
  )
}
