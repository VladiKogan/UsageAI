export type JsonObject = Record<string, unknown>;

export function isObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function getObject(value: unknown, key: string): JsonObject | undefined {
  if (!isObject(value)) {
    return undefined;
  }
  const child = value[key];
  return isObject(child) ? child : undefined;
}

export function getArray(value: unknown, key: string): readonly unknown[] | undefined {
  if (!isObject(value)) {
    return undefined;
  }
  const child = value[key];
  return Array.isArray(child) ? child : undefined;
}

export function getString(value: unknown, key: string): string | undefined {
  if (!isObject(value)) {
    return undefined;
  }
  const child = value[key];
  if (typeof child === "string") {
    return child;
  }
  return typeof child === "number" || typeof child === "boolean" ? String(child) : undefined;
}

export function getNumber(value: unknown, key: string): number | undefined {
  if (!isObject(value)) {
    return undefined;
  }
  const child = value[key];
  return typeof child === "number" && Number.isFinite(child) ? child : undefined;
}

export function getBoolean(value: unknown, key: string): boolean {
  return isObject(value) && value[key] === true;
}

export function parseDate(value: string | undefined): string | undefined {
  if (!value) {
    return undefined;
  }
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? new Date(timestamp).toISOString() : undefined;
}
