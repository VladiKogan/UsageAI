import { mkdtempSync } from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

/**
 * Loaded before every test file (`node --import`). The main Claude and Codex logins are read from
 * the standard folders under the home folder, whatever CLAUDE_CONFIG_DIR / CODEX_HOME say, so the
 * home folder itself is redirected to an empty temporary one: no test can read this machine's real
 * ~/.claude or ~/.codex, or an inherited folder (an agent session running the suite may have
 * CLAUDE_CONFIG_DIR set to a real login). A test that needs either variable sets it itself.
 */
const realHome = os.homedir();
const testHome = mkdtempSync(path.join(os.tmpdir(), "usageai-test-home-"));
process.env.USERPROFILE = testHome;
process.env.HOME = testHome;
delete process.env.CLAUDE_CONFIG_DIR;
delete process.env.CODEX_HOME;

const effectiveHome = process.env.USERPROFILE?.trim() || process.env.HOME?.trim() || realHome;
if (path.resolve(effectiveHome) !== path.resolve(testHome)) {
  throw new Error("The test home redirect did not take: refusing to run against the real home folder.");
}
