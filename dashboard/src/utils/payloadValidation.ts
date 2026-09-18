export interface ValidationResult {
  valid: boolean;
  error?: string;
  payload?: Record<string, unknown>;
}

export const validateQueuePayload = (json: string): ValidationResult => {
  if (!json.trim()) {
    return { valid: false, error: 'Payload cannot be empty' };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch (err) {
    return { valid: false, error: `Invalid JSON: ${(err as Error).message}` };
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { valid: false, error: 'Payload must be a JSON object' };
  }

  return {
    valid: true,
    payload: parsed as Record<string, unknown>
  };
};
