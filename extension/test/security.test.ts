import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import https from "node:https";
import * as os from "node:os";
import * as path from "node:path";
import { Readable } from "node:stream";
import test, { mock } from "node:test";
import {
  collectProcessOutput,
  findExecutable,
  minimalEnvironment,
  parseRetryAfter,
  readBoundedText,
  requestJson,
  spawnSecure,
} from "../src/security";

interface HttpsFixture {
  readonly status?: number;
  readonly headers?: Readonly<Record<string, string>>;
  readonly chunks?: readonly Buffer[];
}

function installHttpsFixture(fixture: HttpsFixture): {
  readonly body: () => string;
  readonly headers: () => Readonly<Record<string, string | number | string[] | undefined>>;
} {
  let written = "";
  let sentHeaders: Readonly<Record<string, string | number | string[] | undefined>> = {};
  mock.method(https, "request", ((
    _url: URL,
    options: { readonly headers?: Readonly<Record<string, string | number | string[] | undefined>> },
    callback: (response: Readable & {
      statusCode?: number;
      headers: Record<string, string>;
    }) => void,
  ) => {
    sentHeaders = options.headers ?? {};
    const request = new EventEmitter() as EventEmitter & {
      write(chunk: string): void;
      end(): void;
      destroy(error?: Error): void;
    };
    request.write = (chunk) => {
      written += chunk;
    };
    request.end = () => {
      const response = Readable.from(fixture.chunks ?? [Buffer.from("{}")]) as Readable & {
        statusCode?: number;
        headers: Record<string, string>;
      };
      response.statusCode = fixture.status ?? 200;
      response.headers = { "content-type": "application/json", ...fixture.headers };
      callback(response);
      queueMicrotask(() => request.emit("close"));
    };
    request.destroy = (error) => {
      if (error) {
        queueMicrotask(() => request.emit("error", error));
      }
      queueMicrotask(() => request.emit("close"));
    };
    return request;
  }) as unknown as typeof https.request);
  return {
    body: () => written,
    headers: () => sentHeaders,
  };
}

test("JSON requests enforce endpoint and response boundaries", async () => {
  await assert.rejects(
    requestJson("http://api.example.com/usage", { allowedHosts: ["api.example.com"] }),
    /unexpected provider endpoint/i,
  );
  await assert.rejects(
    requestJson("https://other.example.com/usage", { allowedHosts: ["api.example.com"] }),
    /unexpected provider endpoint/i,
  );
  await assert.rejects(
    requestJson("https://user:secret@api.example.com/usage", { allowedHosts: ["api.example.com"] }),
    /cannot contain credentials/i,
  );
  // Plain HTTP stays blocked unless the caller opts in, and only for a loopback address.
  await assert.rejects(
    requestJson("http://127.0.0.1:1/usage", { allowedHosts: ["127.0.0.1"] }),
    /unexpected provider endpoint/i,
  );
  await assert.rejects(
    requestJson("http://api.example.com/usage", {
      allowedHosts: ["api.example.com"],
      allowLoopbackPlaintext: true,
    }),
    /unexpected provider endpoint/i,
  );

  try {
    const fixture = installHttpsFixture({
      chunks: [Buffer.from('{"ok":true}')],
      headers: { "content-length": "11" },
    });
    const response = await requestJson<{ readonly ok: boolean }>("https://api.example.com/usage", {
      method: "POST",
      allowedHosts: ["api.example.com"],
      headers: { "Content-Type": "application/json" },
      body: '{"input":1}',
    });
    assert.equal(response.status, 200);
    assert.equal(response.data.ok, true);
    assert.equal(fixture.body(), '{"input":1}');
    assert.equal(fixture.headers()["Content-Length"], "11");
  } finally {
    mock.restoreAll();
  }

  try {
    installHttpsFixture({ status: 302, headers: { location: "https://api.example.com/elsewhere" } });
    await assert.rejects(
      requestJson("https://api.example.com/usage", { allowedHosts: ["api.example.com"] }),
      /redirects are not allowed/i,
    );
  } finally {
    mock.restoreAll();
  }

  try {
    installHttpsFixture({ headers: { "content-type": "text/html" } });
    await assert.rejects(
      requestJson("https://api.example.com/usage", { allowedHosts: ["api.example.com"] }),
      /non-JSON response/i,
    );
  } finally {
    mock.restoreAll();
  }

  try {
    installHttpsFixture({ headers: { "content-length": "1048577" } });
    await assert.rejects(
      requestJson("https://api.example.com/usage", { allowedHosts: ["api.example.com"] }),
      /oversized response/i,
    );
  } finally {
    mock.restoreAll();
  }

  try {
    installHttpsFixture({ chunks: [Buffer.from("not json")] });
    await assert.rejects(
      requestJson("https://api.example.com/usage", { allowedHosts: ["api.example.com"] }),
      /invalid JSON/i,
    );
  } finally {
    mock.restoreAll();
  }
});

test("bounded files, executable lookup, and minimal child environments", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "usageai-extension-security-"));
  const oldPath = process.env.PATH;
  const oldSecret = process.env.USAGEAI_TEST_SECRET;
  try {
    const credentialPath = path.join(directory, "credential.json");
    await writeFile(credentialPath, "small credential", "utf8");
    assert.equal(await readBoundedText(credentialPath, 32), "small credential");
    await assert.rejects(readBoundedText(credentialPath, 4), /unexpectedly large/i);

    const executableName = process.platform === "win32" ? "fixture.cmd" : "fixture";
    const executablePath = path.join(directory, executableName);
    await writeFile(executablePath, "fixture", "utf8");
    process.env.PATH = `relative${path.delimiter}\"${directory}\"`;
    assert.equal(await findExecutable([executableName]), executablePath);

    process.env.USAGEAI_TEST_SECRET = "must-not-leak";
    const environment = minimalEnvironment("USAGEAI_ALLOWED_TEST_VALUE");
    assert.equal(environment.NO_COLOR, "1");
    assert.equal(environment.USAGEAI_TEST_SECRET, undefined);
    assert.throws(() => spawnSecure("relative-command", []), /absolute/i);

    const child = spawnSecure(process.execPath, ["-e", "process.stdout.write('abcdefgh'); process.stderr.write('warning')"]);
    const output = await collectProcessOutput(child, 5_000, 5);
    assert.equal(output.exitCode, 0);
    assert.equal(output.stdout, "abcde");
    assert.equal(output.stderr, "warning");
  } finally {
    if (oldPath === undefined) delete process.env.PATH;
    else process.env.PATH = oldPath;
    if (oldSecret === undefined) delete process.env.USAGEAI_TEST_SECRET;
    else process.env.USAGEAI_TEST_SECRET = oldSecret;
    await rm(directory, { recursive: true, force: true });
  }
});

test("process timeouts and Retry-After parsing are bounded", async () => {
  const child = spawnSecure(process.execPath, ["-e", "setTimeout(() => {}, 10000)"]);
  await assert.rejects(collectProcessOutput(child, 25, 32), /timed out/i);

  assert.equal(parseRetryAfter({ "retry-after": "3" }), 3_000);
  assert.equal(parseRetryAfter({ "retry-after": "invalid" }), 0);
  const future = Date.now() + 30_000;
  const parsed = parseRetryAfter({ "retry-after": new Date(future).toUTCString() });
  assert.ok(parsed >= 28_000 && parsed <= 30_000);
});
