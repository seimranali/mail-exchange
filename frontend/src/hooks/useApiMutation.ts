import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query'
import { useToast } from '@/components/overlays/Toast'
import { errorMessage } from '@/services/apiClient'

interface Options<TData, TVars> {
  mutationFn: (vars: TVars) => Promise<TData>
  /** Query keys to refresh after success. */
  invalidate?: QueryKey[]
  successMessage?: string | ((data: TData, vars: TVars) => string)
  errorTitle?: string
  onSuccess?: (data: TData, vars: TVars) => void
}

/** Mutation with consistent cache invalidation and success/error toasts. */
export function useApiMutation<TData = unknown, TVars = void>({ mutationFn, invalidate = [], successMessage, errorTitle = 'Action failed', onSuccess }: Options<TData, TVars>) {
  const queryClient = useQueryClient()
  const toast = useToast()
  return useMutation({
    mutationFn,
    onSuccess: async (data, vars) => {
      await Promise.all(invalidate.map((queryKey) => queryClient.invalidateQueries({ queryKey })))
      if (successMessage) toast.success(typeof successMessage === 'function' ? successMessage(data, vars) : successMessage)
      onSuccess?.(data, vars)
    },
    onError: (error) => toast.error(errorTitle, errorMessage(error)),
  })
}
