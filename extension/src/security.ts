import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { constants as fsConstants } from "node:fs";
import { access, open, stat } from "node:fs/promises";
import * as http from "node:http";
import * as https from "node:https";
import * as os from "node:os";
import * as path from "node:path";

const MAX_JSON_BYTES = 1_048_576;
const MAX_CREDENTIAL_CHARACTERS = 1_048_576;
const MAX_TOKEN_CHARACTERS = 16_384;

const commonEnvironmentNames = [
  "APPDATA", "COMSPEC", "HOME", "HOMEDRIVE", "HOMEPATH", "HTTP_PROXY",
  "HTTPS_PROXY", "LANG", "LC_ALL", "LOCALAPPDATA", "NO_PROXY", "PATH",
  "PATHEXT", "SystemDrive", "SystemRoot", "TEMP", "TMP", "USERPROFILE", "WINDIR",
] as const;

export interface JsonResponse<T = unknown> {
  readonly status: number;
  readonly headers: http.IncomingHttpHeaders;
  readonly data: T;
}

export interface JsonRequestOptions {
  readonly method?: "GET" | "POST";
  readonly headers?: Readonly<Record<string, string>>;
  readonly body?: string;
  readonly timeoutMs?: number;
  readonly signal?: AbortSignal;
  readonly allowedHosts: readonly string[];
  readonly allowLoopbackSelfSigned?: boolean;
}

export async function requestJson<T = unknown>(
  rawUrl: string | URL,
  options: JsonRequestOptions,
): Promise<JsonResponse<T>> {
  const url = rawUrl instanceof URL ? rawUrl : new URL(rawUrl);
  if (url.protocol !== "https:" || !options.allowedHosts.includes(url.hostname)) {
    throw new Error("Blocked an unexpected provider endpoint.");
  }
  if (url.username || url.password) {
    throw new Error("Provider endpoints cannot contain credentials.");
  }

  const body = options.body;
  const headers: Record<string, string> = {
    Accept: "application/json",
    "User-Agent": "UsageAI-VSCode/0.1.6",
    ...options.headers,
  };
  if (body !== undefined) {
    headers["Content-Length"] = String(Buffer.byteLength(body));
  }

  return new Promise<JsonResponse<T>>((resolve, reject) => {
    const isLoopback = url.hostname === "127.0.0.1" || url.hostname === "localhost";
    const request = https.request(url, {
      method: options.method ?? "GET",
      headers,
      agent: options.allowLoopbackSelfSigned && isLoopback
        ? new https.Agent({ rejectUnauthorized: false })
        : undefined,
    }, (response) => {
      const status = response.statusCode ?? 0;
      if (status >= 300 && status < 400) {
        response.resume();
        reject(new Error("Provider redirects are not allowed."));
        return;
      }

      const contentLength = Number(response.headers["content-length"] ?? 0);
      if (Number.isFinite(contentLength) && contentLength > MAX_JSON_BYTES) {
        response.resume();
        reject(new Error("The provider returned an oversized response."));
        return;
      }

      const contentType = response.headers["content-type"];
      if (typeof contentType === "string" &&
          !contentType.toLowerCase().includes("application/json") &&
          !contentType.toLowerCase().includes("+json")) {
        response.resume();
        reject(new Error("The provider returned a non-JSON response."));
        return;
      }

      const chunks: Buffer[] = [];
      let bytes = 0;
      response.on("data", (chunk: Buffer) => {
        bytes += chunk.length;
        if (bytes > MAX_JSON_BYTES) {
          request.destroy(new Error("The provider returned an oversized response."));
          return;
        }
        chunks.push(chunk);
      });
      response.on("end", () => {
        try {
          const text = Buffer.concat(chunks).toString("utf8");
          resolve({ status, headers: response.headers, data: JSON.parse(text) as T });
        } catch (error) {
          reject(new Error("The provider returned invalid JSON.", { cause: error }));
        }
      });
      response.on("error", reject);
    });

    const timeout = setTimeout(
      () => request.destroy(new Error("The provider request timed out.")),
      options.timeoutMs ?? 15_000,
    );
    timeout.unref();
    request.once("close", () => clearTimeout(timeout));
    request.once("error", reject);
    options.signal?.addEventListener("abort", () => request.destroy(options.signal?.reason), { once: true });
    if (body !== undefined) {
      request.write(body);
    }
    request.end();
  });
}

export async function readBoundedText(
  filePath: string,
  maxCharacters = MAX_CREDENTIAL_CHARACTERS,
): Promise<string> {
  const fileStat = await stat(filePath);
  if (!fileStat.isFile() || fileStat.size > maxCharacters * 4) {
    throw new Error("The credential file is unexpectedly large.");
  }

  const handle = await open(filePath, "r");
  try {
    const buffer = Buffer.alloc(Math.min(fileStat.size + 1, maxCharacters * 4 + 1));
    const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0);
    const value = buffer.subarray(0, bytesRead).toString("utf8");
    if (value.length > maxCharacters) {
      throw new Error("The credential file is unexpectedly large.");
    }
    return value;
  } finally {
    await handle.close();
  }
}

export function normalizeToken(value: string | undefined | null): string | undefined {
  const token = value?.trim();
  if (!token || token.length > MAX_TOKEN_CHARACTERS) {
    return undefined;
  }
  for (const character of token) {
    const code = character.charCodeAt(0);
    if (code < 0x21 || code > 0x7e) {
      return undefined;
    }
  }
  return token;
}

export function userHome(): string {
  return process.env.USERPROFILE?.trim() || process.env.HOME?.trim() || os.homedir();
}

export function minimalEnvironment(...additionalNames: readonly string[]): NodeJS.ProcessEnv {
  const result: NodeJS.ProcessEnv = { NO_COLOR: "1" };
  for (const name of new Set([...commonEnvironmentNames, ...additionalNames])) {
    const value = process.env[name];
    if (value) {
      result[name] = value;
    }
  }
  return result;
}

export async function findExecutable(names: readonly string[]): Promise<string | undefined> {
  const pathValue = process.env.PATH ?? "";
  for (const directory of pathValue.split(path.delimiter)) {
    const cleanDirectory = directory.trim().replace(/^"|"$/g, "");
    if (!path.isAbsolute(cleanDirectory)) {
      continue;
    }
    for (const name of names) {
      const candidate = path.resolve(cleanDirectory, name);
      try {
        await access(candidate, fsConstants.F_OK);
        return candidate;
      } catch {
        // Try the next candidate.
      }
    }
  }
  return undefined;
}

export function spawnSecure(
  executable: string,
  args: readonly string[],
  additionalEnvironmentNames: readonly string[] = [],
): ChildProcessWithoutNullStreams {
  if (!path.isAbsolute(executable)) {
    throw new Error("Child process paths must be absolute.");
  }
  return spawn(executable, args, {
    cwd: userHome(),
    env: minimalEnvironment(...additionalEnvironmentNames),
    shell: false,
    windowsHide: true,
    stdio: "pipe",
  });
}

export async function collectProcessOutput(
  child: ChildProcessWithoutNullStreams,
  timeoutMs: number,
  maxCharacters: number,
): Promise<{ readonly exitCode: number | null; readonly stdout: string; readonly stderr: string }> {
  let stdout = "";
  let stderr = "";
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk: string) => {
    stdout = (stdout + chunk).slice(0, maxCharacters);
  });
  child.stderr.on("data", (chunk: string) => {
    stderr = (stderr + chunk).slice(0, 16_384);
  });

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error("The provider command timed out."));
    }, timeoutMs);
    timer.unref();
    child.once("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.once("close", (exitCode) => {
      clearTimeout(timer);
      resolve({ exitCode, stdout, stderr });
    });
  });
}

export function parseRetryAfter(headers: http.IncomingHttpHeaders): number {
  const value = headers["retry-after"];
  const first = Array.isArray(value) ? value[0] : value;
  if (!first) {
    return 0;
  }
  const seconds = Number(first);
  if (Number.isFinite(seconds)) {
    return Math.max(0, seconds * 1000);
  }
  const date = Date.parse(first);
  return Number.isFinite(date) ? Math.max(0, date - Date.now()) : 0;
}
