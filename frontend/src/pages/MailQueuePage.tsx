import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useAuth } from '@/app/AuthProvider'
import { Button } from '@/components/common/Button'
import { Checkbox } from '@/components/forms/Choice'
import { MailFlowTimeline, MailQueueTable } from '@/components/mail-administration/Operations'
import { ConfirmationDialog } from '@/components/overlays/ConfirmationDialog'
import { Drawer } from '@/components/overlays/Dialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { ContentContainer, PageHeader } from '@/layouts/PageLayout'
import { formatBytes } from '@/lib/format'
import { mailQueueApi } from '@/services/endpoints'
import type { QueuedMessage } from '@/types/api'
import styles from './pages.module.css'

const isGuid = (id: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-/i.test(id)

export default function MailQueuePage() {
  const { hasRole } = useAuth()
  const canManage = hasRole('PlatformAdmin', 'OrgAdmin', 'MailAdmin')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [showFinished, setShowFinished] = useState(false)
  const [confirmCancel, setConfirmCancel] = useState(false)
  const queue = useQuery({ queryKey: ['mail-queue', showFinished], queryFn: () => mailQueueApi.list(200, showFinished), refetchInterval: 15_000 })
  const selected = queue.data?.find((m) => m.queueId === selectedId) ?? null

  const retry = useApiMutation({
    mutationFn: (id: string) => mailQueueApi.retry(id),
    invalidate: [['mail-queue']],
    successMessage: 'Retry scheduled',
    errorTitle: 'Could not retry',
  })
  const cancel = useApiMutation({
    mutationFn: (id: string) => mailQueueApi.cancel(id),
    invalidate: [['mail-queue']],
    successMessage: 'Delivery cancelled',
    errorTitle: 'Could not cancel',
    onSuccess: () => setConfirmCancel(false),
  })

  // Native queue entries (GUID ids) support actions; entries from external MTAs are read-only.
  const actionable = (m: QueuedMessage) => canManage && isGuid(m.queueId) && (m.state === 'Queued' || m.state === 'Deferred')

  return (
    <ContentContainer>
      <PageHeader title="Mail queue" description="Outbound messages awaiting delivery. Deferred messages are retried automatically with increasing delays for up to 5 days."
        actions={<Button icon="refresh" onClick={() => queue.refetch()} loading={queue.isFetching && !queue.isLoading}>Refresh</Button>} />
      <div style={{ marginBottom: 'var(--mx-spacing-3)' }}>
        <Checkbox label="Include delivered and failed messages (last 7 days)" checked={showFinished} onChange={(e) => setShowFinished(e.target.checked)} />
      </div>
      <MailQueueTable rows={queue.data} loading={queue.isFetching} error={queue.error} onRetry={() => queue.refetch()} onSelect={(m) => setSelectedId(m.queueId)} />
      <Drawer open={!!selected} onClose={() => setSelectedId(null)} title={selected?.subject || 'Message delivery'}
        description={selected ? `${selected.sender || '<> (bounce)'} → ${selected.recipient} · ${formatBytes(selected.sizeBytes)}` : undefined}
        footer={selected && actionable(selected) && (
          <>
            <Button variant="ghost" icon="close" onClick={() => setConfirmCancel(true)}>Cancel delivery</Button>
            <Button variant="primary" icon="refresh" loading={retry.isPending} onClick={() => retry.mutate(selected.queueId)}>Retry now</Button>
          </>
        )}>
        {selected && (
          <div className={styles.formStack}>
            <h3 className={styles.sectionTitle}>Delivery history</h3>
            <MailFlowTimeline message={selected} />
          </div>
        )}
      </Drawer>
      <ConfirmationDialog open={confirmCancel} title="Cancel delivery?" destructive confirmLabel="Cancel delivery" cancelLabel="Keep trying" loading={cancel.isPending}
        message={<>The message to <strong>{selected?.recipient}</strong> will not be delivered and no bounce is sent.</>}
        onConfirm={() => selected && cancel.mutate(selected.queueId)} onCancel={() => setConfirmCancel(false)} />
    </ContentContainer>
  )
}
