export interface RecordedRequest {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
}

/** Minimal fake `fetch` for exercising DaprMQClient's REST calls without a live server. */
export function fakeFetch(status: number, jsonBody?: unknown) {
  let lastRequest: RecordedRequest | undefined;

  const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const headers: Record<string, string> = {};
    if (init?.headers) {
      for (const [key, value] of Object.entries(init.headers as Record<string, string>)) {
        headers[key.toLowerCase()] = value;
      }
    }
    lastRequest = {
      url: String(input),
      method: init?.method ?? "GET",
      headers,
      body: typeof init?.body === "string" ? init.body : undefined,
    };

    return new Response(jsonBody !== undefined ? JSON.stringify(jsonBody) : null, {
      status,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;

  return {
    fetch: fetchImpl,
    get lastRequest(): RecordedRequest | undefined {
      return lastRequest;
    },
  };
}
