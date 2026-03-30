import type {
  PushRequest,
  PopResponse,
  AcknowledgeRequest,
  DeadLetterRequest,
  DeadLetterResponse,
  ApiError,
  RegisterSinkRequest,
  RegisterSinkResponse,
  WiremockAdminResponse,
  WiremockRequest,
} from '../types/queue';

const API_BASE = import.meta.env.VITE_API_BASE_URL || 'http://localhost:8002';

export class QueueApiError extends Error {
  constructor(public status: number | string, public data: unknown) {
    super('Queue API Error');
    this.name = 'QueueApiError';
  }
}

export const queueApi = {
  async push(queueId: string, request: PushRequest): Promise<void> {
    const response = await fetch(`${API_BASE}/queue/${queueId}/push`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }
  },

  async pop(queueId: string, count: number = 1): Promise<PopResponse | null> {
    const response = await fetch(`${API_BASE}/queue/${queueId}/pop`, {
      method: 'POST',
      headers: {
        'count': count.toString(),
      },
    });

    if (response.status === 204) {
      return null;
    }

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async popWithAck(queueId: string, count: number = 1, ttlSeconds: number = 30, allowCompeting: boolean = false): Promise<PopResponse | null> {
    const response = await fetch(`${API_BASE}/queue/${queueId}/pop`, {
      method: 'POST',
      headers: {
        'require_ack': 'true',
        'ttl_seconds': ttlSeconds.toString(),
        'allow_competing_consumers': allowCompeting.toString(),
        'count': count.toString(),
      },
    });

    if (response.status === 204) {
      return null;
    }

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async acknowledge(queueId: string, request: AcknowledgeRequest): Promise<void> {
    const response = await fetch(`${API_BASE}/queue/${queueId}/acknowledge`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }
  },

  async deadLetter(queueId: string, request: DeadLetterRequest): Promise<DeadLetterResponse> {
    const response = await fetch(`${API_BASE}/queue/${queueId}/deadletter`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async registerSink(queueId: string, request: RegisterSinkRequest): Promise<RegisterSinkResponse> {
    const response = await fetch(`${API_BASE}/queue/${queueId}/sink/http/register`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async unregisterSink(queueId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/queue/${queueId}/sink/http/unregister`, {
      method: 'POST',
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }
  },

  async probeWiremock(url: string): Promise<boolean> {
    try {
      const urlObj = new URL(url);
      const host = urlObj.hostname === 'host.docker.internal' ? 'localhost' : urlObj.hostname;
      const port = urlObj.port || (urlObj.protocol === 'https:' ? '443' : '80');

      const response = await fetch(`http://${host}:${port}/__admin/mappings`, {
        signal: AbortSignal.timeout(2000),
      });

      return response.ok; // true if 200, false otherwise
    } catch {
      return false;
    }
  },

  async resetWiremockMappings(url: string): Promise<void> {
    const urlObj = new URL(url);
    const host = urlObj.hostname === 'host.docker.internal' ? 'localhost' : urlObj.hostname;
    const port = urlObj.port || (urlObj.protocol === 'https:' ? '443' : '80');

    const resetUrl = `http://${host}:${port}/__admin/mappings/reset`;

    // Use WireMock's reset endpoint to clear all mappings
    const response = await fetch(resetUrl, {
      method: 'POST',
    });

    if (!response.ok) {
      throw new Error(`Failed to reset WireMock mappings: ${response.status}`);
    }
  },

  async applyWiremockMapping(url: string, status: number): Promise<void> {
    const urlObj = new URL(url);
    const host = urlObj.hostname === 'host.docker.internal' ? 'localhost' : urlObj.hostname;
    const port = urlObj.port || (urlObj.protocol === 'https:' ? '443' : '80');

    // Use metadata to identify dashboard-created mappings
    const mapping = {
      request: {
        method: 'POST',
        urlPathPattern: '.*',
      },
      response: {
        status: status,
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ message: `Stub response with status ${status}` }),
      },
      metadata: {
        createdBy: 'daprmq-dashboard',
        statusCode: status,
      },
    };

    const mappingsUrl = `http://${host}:${port}/__admin/mappings`;

    const response = await fetch(mappingsUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(mapping),
    });

    if (!response.ok) {
      throw new Error(`Failed to apply WireMock mapping: ${response.status}`);
    }
  },

  async deleteDashboardWiremockMappings(url: string): Promise<void> {
    const urlObj = new URL(url);
    const host = urlObj.hostname === 'host.docker.internal' ? 'localhost' : urlObj.hostname;
    const port = urlObj.port || (urlObj.protocol === 'https:' ? '443' : '80');

    // Fetch all mappings to find dashboard-created ones
    const response = await fetch(`http://${host}:${port}/__admin/mappings`, {
      signal: AbortSignal.timeout(2000),
    });

    if (!response.ok) {
      throw new Error(`Failed to fetch WireMock mappings: ${response.status}`);
    }

    const data = await response.json();

    // Filter for dashboard-created mappings (metadata.createdBy === 'daprmq-dashboard')
    const dashboardMappings = data.mappings?.filter((m: { id?: string; metadata?: { createdBy?: string } }) =>
      m.metadata?.createdBy === 'daprmq-dashboard'
    ) || [];

    // Delete each dashboard mapping by ID using WireMock's individual delete endpoint
    const deletePromises = dashboardMappings.map(async (mapping: { id: string }) => {
      const deleteResponse = await fetch(`http://${host}:${port}/__admin/mappings/${mapping.id}`, {
        method: 'DELETE',
      });

      if (!deleteResponse.ok) {
        throw new Error(`Failed to delete mapping ${mapping.id}: ${deleteResponse.status}`);
      }
    });

    await Promise.all(deletePromises);
  },

  async fetchWiremockMappings(url: string): Promise<{ mappings: unknown[] }> {
    const urlObj = new URL(url);
    const host = urlObj.hostname === 'host.docker.internal' ? 'localhost' : urlObj.hostname;
    const port = urlObj.port || (urlObj.protocol === 'https:' ? '443' : '80');

    const response = await fetch(`http://${host}:${port}/__admin/mappings`, {
      signal: AbortSignal.timeout(2000),
    });

    if (!response.ok) {
      throw new Error(`WireMock API returned ${response.status}`);
    }

    return response.json();
  },

  async fetchWiremockRequests(url: string): Promise<WiremockAdminResponse> {
    const urlObj = new URL(url);
    const host = urlObj.hostname === 'host.docker.internal' ? 'localhost' : urlObj.hostname;
    const port = urlObj.port || (urlObj.protocol === 'https:' ? '443' : '80');

    const response = await fetch(`http://${host}:${port}/__admin/requests`, {
      signal: AbortSignal.timeout(2000),
    });

    if (!response.ok) {
      throw new Error(`WireMock API returned ${response.status}`);
    }

    const data = await response.json();

    // Parse base64 bodies and extract items with lock metadata
    data.requests = data.requests.map((req: WiremockRequest) => {
      const bodyStr = req.request.bodyAsBase64
        ? atob(req.request.bodyAsBase64)
        : req.request.body;

      // Try to parse items array from request body
      let parsedItems = undefined;
      try {
        const parsed = JSON.parse(bodyStr);
        if (Array.isArray(parsed) && parsed.length > 0) {
          // Check if items have the expected structure (item, priority, lockId, lockExpiresAt)
          if (parsed[0].item !== undefined) {
            parsedItems = parsed;
          }
        }
      } catch {
        // Not JSON or not the expected structure - leave parsedItems as undefined
      }

      return {
        ...req,
        request: {
          ...req.request,
          body: bodyStr
        },
        parsedItems
      };
    });

    return data;
  },
};

export const createApiError = (status: number | string, data: unknown): ApiError => ({
  status,
  data,
});
