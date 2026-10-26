import { useEffect } from 'react'
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { tokenStore } from './apiClient'

/**
 * Connects to the notifications hub while the user is signed in, and refreshes cached
 * dashboard data when the server pushes new health samples.
 */
export function useRealtime(enabled: boolean) {
  const queryClient = useQueryClient()

  useEffect(() => {
    if (!enabled) return
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', { accessTokenFactory: () => tokenStore.get() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('serverHealth', () => {
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] })
    })

    connection.start().catch(() => {
      /* Realtime is an enhancement; the UI keeps working via polling/refetch. */
    })
    return () => {
      void connection.stop()
    }
  }, [enabled, queryClient])
}
