import * as path from 'path';

/**
 * Set to `1` to keep the per-run VS Code profile after a green run so it can be
 * inspected (logs, Cache, settings.json). A failed run keeps it regardless.
 */
export const KEEP_PROFILE_ENV = 'NEXUS_IDE_KEEP_TEST_PROFILE';

export interface TestProfileDirs {
	userDataDir: string;
	extensionsDir: string;
}

/**
 * Resolves the per-run `--user-data-dir` / `--extensions-dir` pair under the
 * VS Code test cache. Both names share one stamp so a preserved pair can be
 * matched to the run that produced it.
 */
export function resolveProfileDirs(cachePath: string, stamp: number, pid: number): TestProfileDirs {
	const suffix = `${stamp}-${pid}`;
	return {
		userDataDir: path.resolve(cachePath, `user-data-${suffix}`),
		extensionsDir: path.resolve(cachePath, `extensions-${suffix}`),
	};
}

/**
 * A profile is kept when the run failed (its logs are the only evidence of the
 * low-frequency flake tracked in #556) or when the operator asked for it with
 * `NEXUS_IDE_KEEP_TEST_PROFILE=1`. Any other value removes it after a green run.
 */
export function shouldKeepProfile(env: NodeJS.ProcessEnv, failed: boolean): boolean {
	return failed || env[KEEP_PROFILE_ENV] === '1';
}
