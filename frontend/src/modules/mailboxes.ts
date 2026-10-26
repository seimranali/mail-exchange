import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useApiMutation } from '@/hooks/useApiMutation'
import { domainsApi, mailboxesApi, type MailboxQuery } from '@/services/endpoints'
import type { CreateMailboxRequest, UpdateMailboxRequest } from '@/types/api'

// Data access for the mailboxes module, kept separate from presentation.

export const mailboxKeys = {
  all: ['mailboxes'] as const,
  list: (q: MailboxQuery) => ['mailboxes', 'list', q] as const,
}

export function useMailboxes(query: MailboxQuery) {
  return useQuery({ queryKey: mailboxKeys.list(query), queryFn: () => mailboxesApi.list(query), placeholderData: keepPreviousData })
}

export function useDomainOptions() {
  return useQuery({
    queryKey: ['domains', 'list'],
    queryFn: () => domainsApi.list(),
    select: (d) => d.items.map((x) => ({ value: x.id, label: x.domainName, isDefault: x.isDefault })),
  })
}

export function useCreateMailbox(onDone: () => void) {
  return useApiMutation({
    mutationFn: (body: CreateMailboxRequest) => mailboxesApi.create(body),
    invalidate: [mailboxKeys.all, ['dashboard']],
    successMessage: (m) => `Mailbox ${m.emailAddress} created`,
    errorTitle: 'Could not create mailbox',
    onSuccess: onDone,
  })
}

export function useUpdateMailbox(onDone?: () => void) {
  return useApiMutation({
    mutationFn: ({ id, body }: { id: string; body: UpdateMailboxRequest }) => mailboxesApi.update(id, body),
    invalidate: [mailboxKeys.all],
    successMessage: 'Mailbox updated',
    errorTitle: 'Could not update mailbox',
    onSuccess: onDone,
  })
}

export function useDeleteMailbox(onDone: () => void) {
  return useApiMutation({
    mutationFn: (id: string) => mailboxesApi.remove(id),
    invalidate: [mailboxKeys.all, ['dashboard']],
    successMessage: 'Mailbox deleted',
    errorTitle: 'Could not delete mailbox',
    onSuccess: onDone,
  })
}

export function useAddAlias(onDone: () => void) {
  return useApiMutation({
    mutationFn: ({ id, localPart, domainId }: { id: string; localPart: string; domainId: string }) => mailboxesApi.addAlias(id, localPart, domainId),
    invalidate: [mailboxKeys.all],
    successMessage: 'Alias added',
    errorTitle: 'Could not add alias',
    onSuccess: onDone,
  })
}
