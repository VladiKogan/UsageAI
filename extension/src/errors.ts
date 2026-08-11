export class UsageProviderError extends Error {
  public readonly retryAfterMs: number;

  public constructor(message: string, retryAfterMs = 0, options?: ErrorOptions) {
    super(message, options);
    this.name = "UsageProviderError";
    this.retryAfterMs = Math.max(0, retryAfterMs);
  }
}

export function safeErrorMessage(error: unknown): string {
  return error instanceof UsageProviderError
    ? error.message
    : "UsageAI could not refresh this provider.";
}
