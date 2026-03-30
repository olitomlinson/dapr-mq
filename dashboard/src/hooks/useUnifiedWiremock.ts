import { useState, useEffect, useMemo, useRef, useCallback } from 'react';
import { queueApi } from '../services/queueApi';
import type { WiremockRequest } from '../types/queue';

export type ResponseStatus = 200 | 202 | 500 | null;

export const useUnifiedWiremock = (sinkUrl: string | undefined, selectedStatus: ResponseStatus) => {
  const [isWiremockDetected, setIsWiremockDetected] = useState(false);
  const [requests, setRequests] = useState<WiremockRequest[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const isReapplyingRef = useRef(false);

  // Unified polling function - combines detection, requests, and reapplication
  const poll = useCallback(async () => {
    if (!sinkUrl) {
      setIsWiremockDetected(false);
      setRequests([]);
      return;
    }

    try {
      setIsLoading(true);

      // Fetch both requests and mappings in parallel
      const [requestsData, mappingsData] = await Promise.all([
        queueApi.fetchWiremockRequests(sinkUrl).catch(() => ({ requests: [], meta: { total: 0 } })),
        queueApi.fetchWiremockMappings(sinkUrl).catch(() => null)
      ]);

      // Update requests
      setRequests(requestsData.requests);
      setError(null);

      // Detection: If mappings fetch succeeded, WireMock is detected
      const detected = mappingsData !== null;
      setIsWiremockDetected(detected);

      // Auto-reapplication: Check if dashboard mapping exists for selected status
      if (detected && selectedStatus !== null && !isReapplyingRef.current) {
        const mappings = (mappingsData?.mappings || []) as Array<{
          id?: string;
          metadata?: { createdBy?: string; statusCode?: number };
          response?: { status?: number };
        }>;

        // Check if ANY mapping exists with the correct status (not just dashboard-created ones)
        // This prevents creating duplicates if a mapping already exists from manual application
        const mappingExists = mappings.some((m) =>
          m.metadata?.createdBy === 'daprmq-dashboard' &&
          m.metadata?.statusCode === selectedStatus &&
          m.response?.status === selectedStatus
        );

        // If dashboard mapping doesn't exist, reapply it
        if (!mappingExists) {
          isReapplyingRef.current = true;
          try {
            await queueApi.applyWiremockMapping(sinkUrl, selectedStatus);
          } catch (err) {
            // Silently fail - will retry on next cycle
          } finally {
            isReapplyingRef.current = false;
          }
        }
      }
    } catch (err) {
      // Silently handle errors - detection status will show in UI
      setError((err as Error).message);
      setIsWiremockDetected(false);
    } finally {
      setIsLoading(false);
    }
  }, [sinkUrl, selectedStatus]);

  // Unified polling effect - single 2.5s interval
  useEffect(() => {
    if (!sinkUrl) {
      setIsWiremockDetected(false);
      setRequests([]);
      return;
    }

    poll(); // Immediate execution
    const interval = setInterval(poll, 2500);

    return () => clearInterval(interval);
  }, [sinkUrl, selectedStatus, poll]);

  // Count total messages across all requests
  const messageCount = useMemo(() => {
    return requests.reduce((total, req) => {
      try {
        const body = JSON.parse(req.request.body);
        if (Array.isArray(body)) {
          return total + body.length;
        }
        return total + 1; // Single message
      } catch {
        return total;
      }
    }, 0);
  }, [requests]);

  // Block auto-reapplication temporarily (called before manual application)
  const blockAutoReapplication = useCallback(() => {
    isReapplyingRef.current = true;
  }, []);

  // Unblock auto-reapplication (called after manual application completes)
  const unblockAutoReapplication = useCallback(() => {
    isReapplyingRef.current = false;
  }, []);

  return {
    isWiremockDetected,
    requests,
    isLoading,
    error,
    messageCount,
    blockAutoReapplication,
    unblockAutoReapplication
  };
};
