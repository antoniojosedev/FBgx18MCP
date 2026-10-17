import * as assert from "assert";
import * as path from "path";
import {
  KEEP_PROFILE_ENV,
  resolveProfileDirs,
  shouldKeepProfile,
} from "../testProfile";

// #556: the runner creates one VS Code profile per run and removes it after a
// green run. These cases pin the naming and the keep/remove decision.
suite("testProfile - per-run VS Code test profile (#556)", () => {
  const cachePath = path.resolve("cache-root");

  test("resolveProfileDirs puts both directories under the cache path with one shared stamp", () => {
    const dirs = resolveProfileDirs(cachePath, 1700000000000, 4242);
    assert.strictEqual(
      dirs.userDataDir,
      path.join(cachePath, "user-data-1700000000000-4242"),
    );
    assert.strictEqual(
      dirs.extensionsDir,
      path.join(cachePath, "extensions-1700000000000-4242"),
    );
  });

  test("resolveProfileDirs yields distinct pairs for distinct runs", () => {
    const a = resolveProfileDirs(cachePath, 1, 10);
    const b = resolveProfileDirs(cachePath, 2, 10);
    const c = resolveProfileDirs(cachePath, 1, 11);
    assert.notStrictEqual(a.userDataDir, b.userDataDir);
    assert.notStrictEqual(a.userDataDir, c.userDataDir);
    assert.notStrictEqual(a.extensionsDir, b.extensionsDir);
    assert.notStrictEqual(a.userDataDir, a.extensionsDir);
  });

  test("shouldKeepProfile removes the profile after a green run by default", () => {
    assert.strictEqual(shouldKeepProfile({}, false), false);
  });

  test("shouldKeepProfile keeps the profile after a failed run", () => {
    assert.strictEqual(shouldKeepProfile({}, true), true);
    assert.strictEqual(shouldKeepProfile({ [KEEP_PROFILE_ENV]: "0" }, true), true);
  });

  test(`shouldKeepProfile keeps the profile when ${KEEP_PROFILE_ENV}=1`, () => {
    assert.strictEqual(shouldKeepProfile({ [KEEP_PROFILE_ENV]: "1" }, false), true);
  });

  test(`shouldKeepProfile ignores other ${KEEP_PROFILE_ENV} values`, () => {
    assert.strictEqual(shouldKeepProfile({ [KEEP_PROFILE_ENV]: "0" }, false), false);
    assert.strictEqual(shouldKeepProfile({ [KEEP_PROFILE_ENV]: "" }, false), false);
    assert.strictEqual(shouldKeepProfile({ [KEEP_PROFILE_ENV]: "true" }, false), false);
  });
});
