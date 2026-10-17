import * as fs from 'fs';
import * as path from 'path';
import { downloadAndUnzipVSCode, runTests } from '@vscode/test-electron';
import { KEEP_PROFILE_ENV, resolveProfileDirs, shouldKeepProfile, TestProfileDirs } from './testProfile';

function describeExit(err: unknown): { exitCode?: number; signal?: string } {
	// TestRunFailedError from @vscode/test-electron carries the Electron exit code/signal.
	const detail = err as { code?: unknown; signal?: unknown } | null;
	return {
		exitCode: typeof detail?.code === 'number' ? detail.code : undefined,
		signal: typeof detail?.signal === 'string' ? detail.signal : undefined,
	};
}

async function cleanupProfile(profile: TestProfileDirs, failed: boolean): Promise<void> {
	const dirs = [profile.userDataDir, profile.extensionsDir];
	if (shouldKeepProfile(process.env, failed)) {
		const reason = failed ? 'run failed' : `${KEEP_PROFILE_ENV}=1`;
		console.warn(`Keeping VS Code test profile (${reason}): ${dirs.join(' ; ')}`);
		return;
	}
	for (const dir of dirs) {
		try {
			// Electron may still hold Cache/GPUCache handles briefly after exit on Windows.
			await fs.promises.rm(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
		} catch (err) {
			console.warn(`Could not remove VS Code test profile directory ${dir}:`, err);
		}
	}
}

async function main() {
	let vscodeExecutablePath: string | undefined;
	let profile: TestProfileDirs | undefined;
	let launchArgs: string[] | undefined;
	let failed = false;
	try {
		process.env.NEXUS_IDE_TEST_MODE = '1';

		// The folder containing the Extension Manifest package.json
		// Passed to `--extensionDevelopmentPath`
		const extensionDevelopmentPath = path.resolve(__dirname, '../../');

		// The path to test runner
		// Passed to --extensionTestsPath
		const extensionTestsPath = path.resolve(__dirname, './suite/index');

		const version = process.env.NEXUS_IDE_VSCODE_VERSION || '1.111.0';
		const cachePath = path.resolve(__dirname, '../../.vscode-test');
		const maxAttempts = 4;
		let lastDownloadError: unknown;

		for (let attempt = 1; attempt <= maxAttempts; attempt++) {
			try {
				// Download VS Code into the workflow-cached directory. The retry is bounded
				// and only covers transient CDN/network failures.
				vscodeExecutablePath = await downloadAndUnzipVSCode({
					version,
					cachePath,
					timeout: 120000,
				});
				break;
			} catch (err) {
				lastDownloadError = err;
				if (attempt === maxAttempts) throw err;
				const delayMs = Math.min(30000, 2000 * 2 ** (attempt - 1));
				console.warn(`VS Code integration setup failed (attempt ${attempt}/${maxAttempts}); retrying in ${delayMs}ms`, err);
				await new Promise((resolve) => setTimeout(resolve, delayMs));
			}
		}

		if (!vscodeExecutablePath) throw lastDownloadError;

		// Per-run profile: isolates concurrent runs from the shared-profile
		// `claimInstance` abort and is removed by cleanupProfile after a green run.
		profile = resolveProfileDirs(cachePath, Date.now(), process.pid);
		launchArgs = [
			`--user-data-dir=${profile.userDataDir}`,
			`--extensions-dir=${profile.extensionsDir}`,
			'--disable-updates',
			'--skip-welcome',
			'--skip-release-notes',
			'--disable-workspace-trust',
			'--disable-gpu',
			'--enable-proposed-api',
			'lennix1337.nexus-ide',
		];

		// Keep functional test failures outside the infrastructure retry loop.
		await runTests({
			extensionDevelopmentPath,
			extensionTestsPath,
			version,
			vscodeExecutablePath,
			timeout: 120000,
			launchArgs,
		});
	} catch (err) {
		failed = true;
		console.error('Failed to run tests with detail:', err);
		// #556: the low-frequency flake has no known cause. Record the launch context
		// (no environment dump) so a failed run can be compared with a green one.
		console.error('Test runner failure context:', JSON.stringify({
			cwd: process.cwd(),
			argv: process.argv,
			vscodeExecutablePath,
			launchArgs,
			userDataDir: profile?.userDataDir,
			extensionsDir: profile?.extensionsDir,
			...describeExit(err),
		}, null, 2));
	} finally {
		if (profile) await cleanupProfile(profile, failed);
	}
	if (failed) process.exit(1);
}

main();
