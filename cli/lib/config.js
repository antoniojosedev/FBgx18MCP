const path = require('path');
const fs = require('fs');
const os = require('os');

function generateConfig(gxPath, kbPath) {
    return {
        GeneXus: { InstallationPath: gxPath },
        Server: { HttpPort: 5000, McpStdio: true, SessionIdleTimeoutMinutes: 10, WorkerIdleTimeoutMinutes: 5 },
        Environment: { KBPath: kbPath }
    };
}

function getGatewayExePath() {
    if (process.env.GENEXUS_MCP_GATEWAY_EXE) {
        return process.env.GENEXUS_MCP_GATEWAY_EXE;
    }
    return path.join(__dirname, '..', '..', 'publish', 'GxMcp.Gateway.exe');
}

function getToolDefinitionsPath() {
    return path.join(__dirname, '..', '..', 'src', 'GxMcp.Gateway', 'tool_definitions.json');
}

function discoverGeneXusFromRegistry() {
    if (process.platform !== 'win32') return null;
    try {
        const { execFileSync } = require('child_process');
        const versions = ['GeneXus 18', 'GeneXus 17', 'GeneXus 16'];
        const hives = [
            'HKLM\\SOFTWARE\\WOW6432Node\\Artech',
            'HKLM\\SOFTWARE\\Artech',
            'HKCU\\SOFTWARE\\Artech'
        ];
        for (const hive of hives) {
            for (const ver of versions) {
                const key = `${hive}\\${ver}`;
                try {
                    const out = execFileSync('reg.exe', ['query', key, '/v', 'InstallationDirectory'], {
                        encoding: 'utf8',
                        stdio: ['ignore', 'pipe', 'ignore'],
                        windowsHide: true,
                        timeout: 3000
                    });
                    const match = out.match(/InstallationDirectory\s+REG_SZ\s+(.+?)\r?\n/i);
                    if (match) {
                        const candidate = match[1].trim().replace(/[\\/]+$/, '');
                        if (candidate && fs.existsSync(path.join(candidate, 'genexus.exe'))) {
                            return candidate;
                        }
                    }
                } catch {
                }
            }
        }
    } catch {
    }
    return null;
}

function discoverGeneXusInstallation() {
    if (process.env.GENEXUS_HOME) {
        const candidate = process.env.GENEXUS_HOME.replace(/[\\/]+$/, '');
        if (fs.existsSync(path.join(candidate, 'genexus.exe'))) return candidate;
    }

    const fromRegistry = discoverGeneXusFromRegistry();
    if (fromRegistry) return fromRegistry;

    const programDirs = [];
    if (process.env['ProgramFiles(x86)']) programDirs.push(process.env['ProgramFiles(x86)']);
    if (process.env.ProgramFiles) programDirs.push(process.env.ProgramFiles);
    // Cover non-default drives where IT often installs SDKs.
    for (const drive of ['C', 'D', 'E']) {
        programDirs.push(`${drive}:\\Program Files (x86)`);
        programDirs.push(`${drive}:\\Program Files`);
    }

    const versions = ['GeneXus18', 'GeneXus17', 'GeneXus16'];
    const seen = new Set();
    for (const base of programDirs) {
        const root = path.join(base, 'GeneXus');
        const key = root.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        for (const ver of versions) {
            const candidate = path.join(root, ver);
            if (fs.existsSync(path.join(candidate, 'genexus.exe'))) {
                return candidate;
            }
        }
        // Also scan any GeneXus* sibling (e.g. custom-named "GeneXus18 U10").
        try {
            if (fs.existsSync(root)) {
                for (const entry of fs.readdirSync(root)) {
                    if (!/^GeneXus/i.test(entry)) continue;
                    const candidate = path.join(root, entry);
                    if (fs.existsSync(path.join(candidate, 'genexus.exe'))) {
                        return candidate;
                    }
                }
            }
        } catch {
        }
    }

    const fromPath = discoverGeneXusFromPath();
    if (fromPath) return fromPath;

    return null;
}

function discoverGeneXusFromPath() {
    if (process.platform !== 'win32') return null;
    try {
        const { execFileSync } = require('child_process');
        const out = execFileSync('where.exe', ['genexus.exe'], {
            encoding: 'utf8',
            stdio: ['ignore', 'pipe', 'ignore'],
            windowsHide: true,
            timeout: 3000
        });
        const first = out.split(/\r?\n/).map((s) => s.trim()).find(Boolean);
        if (first && fs.existsSync(first)) {
            return path.dirname(first);
        }
    } catch {
    }
    return null;
}

function discoverKnowledgeBase(cwd) {
    if (!cwd) return null;
    if (directoryLooksLikeKnowledgeBase(cwd)) return cwd;
    return null;
}

function directoryLooksLikeKnowledgeBase(dir) {
    try {
        const files = fs.readdirSync(dir);
        return files.some((f) => f.toLowerCase().endsWith('.gxw') || f.toLowerCase() === 'knowledgebase.connection');
    } catch {
        return false;
    }
}

function readJsonFileSafe(filePath) {
    try {
        const raw = fs.readFileSync(filePath, 'utf8').replace(/^\uFEFF/, '');
        if (!raw.trim()) return {};
        return JSON.parse(raw);
    } catch {
        return null;
    }
}

function resolveConfigPathNoMutate(cwd) {
    const cwdConfigPath = path.join(cwd, 'config.json');
    if (process.env.GX_CONFIG_PATH && fs.existsSync(process.env.GX_CONFIG_PATH)) {
        return process.env.GX_CONFIG_PATH;
    }
    if (fs.existsSync(cwdConfigPath)) {
        return cwdConfigPath;
    }
    return null;
}

function createConfigFile(kbPath, gxPath) {
    const targetConfigPath = path.join(kbPath, 'config.json');
    const baseConfig = generateConfig(gxPath, kbPath);

    if (!fs.existsSync(kbPath)) {
        fs.mkdirSync(kbPath, { recursive: true });
    }

    const existing = fs.existsSync(targetConfigPath) ? readJsonFileSafe(targetConfigPath) : null;
    const preservedEnv = {};
    if (existing && existing.Environment) {
        if (existing.Environment.KBs) preservedEnv.KBs = existing.Environment.KBs;
        if (existing.Environment.ActiveKb) preservedEnv.ActiveKb = existing.Environment.ActiveKb;
    }
    const nextConfig = {
        ...baseConfig,
        Environment: { ...baseConfig.Environment, ...preservedEnv }
    };

    const changed = !existing || JSON.stringify(existing) !== JSON.stringify(nextConfig);
    if (changed) {
        fs.writeFileSync(targetConfigPath, JSON.stringify(nextConfig, null, 2));
    }

    return {
        targetConfigPath,
        config: nextConfig,
        changed
    };
}

function getLauncher() {
    // Set by scripts/install.ps1 for fixed-path corporate installs — clients
    // launch the gateway exe directly instead of resolving via the npx cache.
    const directExe = process.env.GENEXUS_MCP_GATEWAY_EXE;
    return directExe
        ? { command: directExe, args: [] }
        : { command: process.platform === 'win32' ? 'npx.cmd' : 'npx', args: ['-y', 'genexus-mcp@latest'] };
}

function getClientConfigTargets() {
    const home = os.homedir();
    const xdgConfig = process.env.XDG_CONFIG_HOME || path.join(home, '.config');
    return [
        {
            id: 'claude-desktop-win',
            name: 'Claude Desktop (Windows)',
            format: 'mcpServers',
            path: path.join(home, 'AppData', 'Roaming', 'Claude', 'claude_desktop_config.json'),
            platforms: ['win32']
        },
        {
            id: 'claude-desktop-mac',
            name: 'Claude Desktop (macOS)',
            format: 'mcpServers',
            path: path.join(home, 'Library', 'Application Support', 'Claude', 'claude_desktop_config.json'),
            platforms: ['darwin']
        },
        {
            id: 'antigravity',
            name: 'Antigravity',
            format: 'mcpServers',
            path: path.join(home, '.gemini', 'antigravity', 'mcp_config.json')
        },
        {
            id: 'claude-code',
            name: 'Claude Code',
            format: 'mcpServers',
            path: path.join(home, '.claude.json')
        },
        {
            id: 'gemini-cli',
            name: 'Gemini CLI',
            format: 'mcpServers',
            path: path.join(home, '.gemini', 'settings.json')
        },
        {
            id: 'cursor',
            name: 'Cursor',
            format: 'mcpServers',
            path: path.join(home, '.cursor', 'mcp.json')
        },
        {
            id: 'opencode',
            name: 'OpenCode',
            format: 'opencode',
            path: path.join(xdgConfig, 'opencode', 'opencode.json')
        },
        {
            id: 'codex-cli',
            name: 'Codex CLI',
            format: 'codex-toml',
            path: path.join(home, '.codex', 'config.toml')
        }
    ];
}

function listSupportedClientIds() {
    return getClientConfigTargets().map((c) => c.id);
}

function filterClientTargets(targets, opts = {}) {
    const { ids, onlyExisting, platform } = opts;
    let out = targets;
    if (platform) out = out.filter((c) => !c.platforms || c.platforms.includes(platform));
    if (ids && ids.length) {
        const set = new Set(ids);
        out = out.filter((c) => set.has(c.id));
    }
    if (onlyExisting) out = out.filter((c) => fs.existsSync(c.path));
    return out;
}

function patchClientConfig(targetConfigPath, opts = {}) {
    // Validate corporate-install env var before we write it into N client configs.
    // Otherwise we silently propagate a broken path to every AI client and the
    // user only finds out when each one fails with "Failed to connect".
    if (process.env.GENEXUS_MCP_GATEWAY_EXE && !fs.existsSync(process.env.GENEXUS_MCP_GATEWAY_EXE)) {
        const err = new Error(
            `GENEXUS_MCP_GATEWAY_EXE points to a path that does not exist: ${process.env.GENEXUS_MCP_GATEWAY_EXE}. ` +
            `Refusing to write this into client configs. Unset the env var (to use the npx launcher) or re-run scripts/install.ps1 to materialize the exe.`
        );
        err.code = 'GATEWAY_EXE_MISSING';
        throw err;
    }

    const launcher = getLauncher();
    const onlyExisting = opts.onlyExisting !== false;
    const candidates = filterClientTargets(getClientConfigTargets(), {
        ids: opts.ids,
        platform: process.platform
    });

    const patched = [];
    const failed = [];
    const skipped = [];

    for (const client of candidates) {
        if (onlyExisting && !fs.existsSync(client.path)) {
            skipped.push({ client: client.name, reason: 'not installed' });
            continue;
        }
        try {
            fs.mkdirSync(path.dirname(client.path), { recursive: true });
            applyClientEntry(client, launcher, targetConfigPath);
            patched.push(client.name);
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { patched, failed, skipped };
}

function unpatchClientConfig(opts = {}) {
    const targets = filterClientTargets(getClientConfigTargets(), {
        ids: opts.ids,
        onlyExisting: true,
        platform: process.platform
    });
    const removed = [];
    const skipped = [];
    const failed = [];

    for (const client of targets) {
        try {
            const wasRemoved = removeClientEntry(client);
            if (wasRemoved) removed.push(client.name);
            else skipped.push({ client: client.name, reason: 'no genexus entry' });
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { removed, skipped, failed };
}

function applyClientEntry(client, launcher, targetConfigPath) {
    switch (client.format) {
        case 'mcpServers':
            return applyMcpServersJson(client.path, launcher, targetConfigPath);
        case 'opencode':
            return applyOpenCodeJson(client.path, launcher, targetConfigPath);
        case 'codex-toml':
            return applyCodexToml(client.path, launcher, targetConfigPath);
        default:
            throw new Error(`Unknown client format: ${client.format}`);
    }
}

function removeClientEntry(client) {
    switch (client.format) {
        case 'mcpServers':
            return removeMcpServersJson(client.path);
        case 'opencode':
            return removeOpenCodeJson(client.path);
        case 'codex-toml':
            return removeCodexToml(client.path);
        default:
            throw new Error(`Unknown client format: ${client.format}`);
    }
}

function applyMcpServersJson(filePath, launcher, targetConfigPath) {
    const parsed = fs.existsSync(filePath) ? readJsonFileSafe(filePath) : {};
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    cfgObj.mcpServers = cfgObj.mcpServers || {};
    cfgObj.mcpServers.genexus = { ...launcher, env: { GX_CONFIG_PATH: targetConfigPath } };
    fs.writeFileSync(filePath, JSON.stringify(cfgObj, null, 2));
}

function removeMcpServersJson(filePath) {
    const parsed = readJsonFileSafe(filePath);
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    if (!cfgObj.mcpServers || !cfgObj.mcpServers.genexus) return false;
    delete cfgObj.mcpServers.genexus;
    fs.writeFileSync(filePath, JSON.stringify(cfgObj, null, 2));
    return true;
}

// OpenCode (sst/opencode) uses `mcp.<name>` with `type: 'local'` and `command: string[]`.
function applyOpenCodeJson(filePath, launcher, targetConfigPath) {
    const parsed = fs.existsSync(filePath) ? readJsonFileSafe(filePath) : {};
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    cfgObj.mcp = cfgObj.mcp || {};
    cfgObj.mcp.genexus = {
        type: 'local',
        command: [launcher.command, ...(launcher.args || [])],
        environment: { GX_CONFIG_PATH: targetConfigPath },
        enabled: true
    };
    fs.writeFileSync(filePath, JSON.stringify(cfgObj, null, 2));
}

function removeOpenCodeJson(filePath) {
    const parsed = readJsonFileSafe(filePath);
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    if (!cfgObj.mcp || !cfgObj.mcp.genexus) return false;
    delete cfgObj.mcp.genexus;
    fs.writeFileSync(filePath, JSON.stringify(cfgObj, null, 2));
    return true;
}

// Codex CLI uses TOML. We do a minimal text-merge: strip any existing
// [mcp_servers.genexus*] blocks and append fresh ones. Brittle on hand-edited
// files that put other keys after our blocks without a blank line, but
// adequate for the typical machine-managed config.
function applyCodexToml(filePath, launcher, targetConfigPath) {
    let existing = '';
    if (fs.existsSync(filePath)) existing = fs.readFileSync(filePath, 'utf8');
    const stripped = stripCodexGenexusBlocks(existing);
    const args = launcher.args || [];
    const lines = [];
    if (stripped.length && !stripped.endsWith('\n')) lines.push('');
    if (stripped.length) lines.push('');
    lines.push('[mcp_servers.genexus]');
    lines.push(`command = ${tomlString(launcher.command)}`);
    lines.push(`args = [${args.map(tomlString).join(', ')}]`);
    lines.push('');
    lines.push('[mcp_servers.genexus.env]');
    lines.push(`GX_CONFIG_PATH = ${tomlString(targetConfigPath)}`);
    lines.push('');
    fs.writeFileSync(filePath, stripped + lines.join('\n'));
}

function removeCodexToml(filePath) {
    if (!fs.existsSync(filePath)) return false;
    const existing = fs.readFileSync(filePath, 'utf8');
    const stripped = stripCodexGenexusBlocks(existing);
    if (stripped === existing) return false;
    fs.writeFileSync(filePath, stripped);
    return true;
}

function stripCodexGenexusBlocks(content) {
    // Walk line-by-line so values like `args = []` don't confuse the parser.
    // A section ends at the next line that starts a [section] header (top-level
    // tables and arrays of tables only — must begin at column 0).
    const lines = content.split('\n');
    const out = [];
    const headerRe = /^\[\[?[A-Za-z0-9_."'-]+/;
    const ourRe = /^\[\[?mcp_servers\.genexus(\.[A-Za-z0-9_."'-]+)?\]\]?\s*$/;
    let inOurs = false;
    for (const line of lines) {
        if (headerRe.test(line)) {
            inOurs = ourRe.test(line);
            if (inOurs) continue;
        }
        if (inOurs) continue;
        out.push(line);
    }
    // Collapse 3+ trailing blank lines we may have left behind.
    return out.join('\n').replace(/\n{3,}/g, '\n\n');
}

function tomlString(value) {
    const s = String(value);
    return `"${s.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

function isPathLikelyAppLockerBlocked(exePath) {
    if (process.platform !== 'win32' || !exePath) return null;
    const norm = String(exePath).toLowerCase().replace(/\\/g, '/');
    const candidates = [
        { name: 'APPDATA', base: process.env.APPDATA },
        { name: 'LOCALAPPDATA', base: process.env.LOCALAPPDATA },
        { name: 'TEMP', base: process.env.TEMP },
        { name: 'TMP', base: process.env.TMP }
    ];
    for (const { name, base } of candidates) {
        if (!base) continue;
        const baseNorm = base.toLowerCase().replace(/\\/g, '/').replace(/\/$/, '');
        if (norm.startsWith(baseNorm + '/')) return name;
    }
    return null;
}

function normalizeExePath(p) {
    if (!p) return '';
    let s = String(p).trim().replace(/^"|"$/g, '');
    s = s.replace(/\\/g, '/').replace(/\/+$/, '');
    if (process.platform === 'win32') s = s.toLowerCase();
    return s;
}

function readClientCommandEntry(client) {
    if (!fs.existsSync(client.path)) return null;
    try {
        if (client.format === 'mcpServers') {
            const parsed = readJsonFileSafe(client.path);
            if (!parsed || typeof parsed !== 'object') return null;
            const entry = parsed.mcpServers && parsed.mcpServers.genexus;
            if (!entry) return null;
            return { command: entry.command || null, args: Array.isArray(entry.args) ? entry.args : [] };
        }
        if (client.format === 'opencode') {
            const parsed = readJsonFileSafe(client.path);
            if (!parsed || typeof parsed !== 'object') return null;
            const entry = parsed.mcp && parsed.mcp.genexus;
            if (!entry || !Array.isArray(entry.command) || entry.command.length === 0) return null;
            return { command: entry.command[0], args: entry.command.slice(1) };
        }
        if (client.format === 'codex-toml') {
            const raw = fs.readFileSync(client.path, 'utf8');
            // Minimal extraction: find [mcp_servers.genexus] block and pull command = "..."
            const blockRe = /\[mcp_servers\.genexus\]([\s\S]*?)(?=\n\[|$)/;
            const m = raw.match(blockRe);
            if (!m) return null;
            const cmdMatch = m[1].match(/^\s*command\s*=\s*"((?:[^"\\]|\\.)*)"/m);
            if (!cmdMatch) return null;
            const command = cmdMatch[1].replace(/\\\\/g, '\\').replace(/\\"/g, '"');
            return { command, args: [] };
        }
    } catch {
        return null;
    }
    return null;
}

function getLocalAppDataCacheDir() {
    if (process.platform !== 'win32') return null;
    const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(base, 'GenexusMCP');
}

function readGeneXusVersionFromInstall(gxPath) {
    if (!gxPath) return null;
    const candidates = [
        path.join(gxPath, 'version.txt'),
        path.join(gxPath, 'Version.txt'),
        path.join(gxPath, 'GeneXus.version')
    ];
    for (const candidate of candidates) {
        try {
            const raw = fs.readFileSync(candidate, 'utf8').trim();
            if (raw) return raw.split(/\r?\n/)[0].trim();
        } catch {
        }
    }
    return null;
}

function readKbCatalog(configPath) {
    if (!configPath) return { kbs: {}, activeKb: null, kbPath: null };
    const cfg = readJsonFileSafe(configPath);
    if (!cfg) return { kbs: {}, activeKb: null, kbPath: null };
    const env = cfg.Environment || {};
    return {
        kbs: (env.KBs && typeof env.KBs === 'object') ? env.KBs : {},
        activeKb: typeof env.ActiveKb === 'string' ? env.ActiveKb : null,
        kbPath: typeof env.KBPath === 'string' ? env.KBPath : null
    };
}

function writeKbCatalog(configPath, { kbs, activeKb, kbPath }) {
    const cfg = readJsonFileSafe(configPath) || {};
    cfg.Environment = cfg.Environment || {};
    cfg.Environment.KBs = kbs;
    if (activeKb) cfg.Environment.ActiveKb = activeKb;
    else delete cfg.Environment.ActiveKb;
    if (kbPath) cfg.Environment.KBPath = kbPath;
    else delete cfg.Environment.KBPath;
    fs.writeFileSync(configPath, JSON.stringify(cfg, null, 2));
}

function addKbToConfig(configPath, name, kbPath) {
    const catalog = readKbCatalog(configPath);
    const alreadyRegistered = catalog.kbs[name] === kbPath;
    const willBecomeActive = !catalog.activeKb;
    if (alreadyRegistered && !willBecomeActive) {
        return catalog;
    }
    catalog.kbs[name] = kbPath;
    if (willBecomeActive) {
        catalog.activeKb = name;
        catalog.kbPath = kbPath;
    }
    writeKbCatalog(configPath, catalog);
    return catalog;
}

function removeKbFromConfig(configPath, name) {
    const catalog = readKbCatalog(configPath);
    if (!(name in catalog.kbs)) return { catalog, removed: false };
    delete catalog.kbs[name];
    if (catalog.activeKb === name) {
        const remainingNames = Object.keys(catalog.kbs);
        catalog.activeKb = remainingNames[0] || null;
        catalog.kbPath = catalog.activeKb ? catalog.kbs[catalog.activeKb] : null;
    }
    writeKbCatalog(configPath, catalog);
    return { catalog, removed: true };
}

function switchActiveKb(configPath, { name, path: explicitPath }) {
    const catalog = readKbCatalog(configPath);

    let targetName = name;
    let targetPath = explicitPath;

    if (explicitPath && !name) {
        const existing = Object.entries(catalog.kbs).find(([, p]) => p === explicitPath);
        if (existing) {
            targetName = existing[0];
        } else {
            targetName = path.basename(explicitPath);
            if (catalog.kbs[targetName] && catalog.kbs[targetName] !== explicitPath) {
                return {
                    ok: false,
                    reason: `Name '${targetName}' is already registered to a different path (${catalog.kbs[targetName]}). Pass --name explicitly to disambiguate.`
                };
            }
            catalog.kbs[targetName] = explicitPath;
        }
    } else if (name) {
        if (!(name in catalog.kbs)) {
            return { ok: false, reason: `KB '${name}' is not registered. Use \`genexus-mcp kb add\` first or pass --path.` };
        }
        targetPath = catalog.kbs[name];
    } else {
        return { ok: false, reason: 'Either --name or --path is required.' };
    }

    catalog.activeKb = targetName;
    catalog.kbPath = targetPath;
    writeKbCatalog(configPath, catalog);
    return { ok: true, catalog, switchedTo: { name: targetName, path: targetPath } };
}

function applyLauncherConfigOrExit({ cwd, stderr, quiet }) {
    const log = (msg) => {
        if (!quiet) stderr.write(`${msg}\n`);
    };

    const cwdConfigPath = path.join(cwd, 'config.json');

    if (process.env.GX_CONFIG_PATH) {
        return { ok: true };
    }

    if (fs.existsSync(cwdConfigPath)) {
        process.env.GX_CONFIG_PATH = cwdConfigPath;
        return { ok: true };
    }

    const foundGxPath = discoverGeneXusInstallation();
    if (!foundGxPath) {
        log('[genexus-mcp] ERROR: No config.json found and GeneXus installation auto-discovery failed.');
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    if (!directoryLooksLikeKnowledgeBase(cwd)) {
        log('[genexus-mcp] ERROR: Zero-config failed because current directory is not a GeneXus KB.');
        log(`[genexus-mcp] CWD: ${cwd}`);
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    log(`[genexus-mcp] Auto-discovered GeneXus at: ${foundGxPath}`);
    log(`[genexus-mcp] Generating default config.json for KB at: ${cwd}`);

    const defaultConfig = generateConfig(foundGxPath, cwd);
    fs.writeFileSync(cwdConfigPath, JSON.stringify(defaultConfig, null, 2));
    process.env.GX_CONFIG_PATH = cwdConfigPath;

    return { ok: true };
}

module.exports = {
    generateConfig,
    getGatewayExePath,
    getToolDefinitionsPath,
    discoverGeneXusInstallation,
    discoverGeneXusFromRegistry,
    discoverKnowledgeBase,
    directoryLooksLikeKnowledgeBase,
    readJsonFileSafe,
    resolveConfigPathNoMutate,
    createConfigFile,
    patchClientConfig,
    unpatchClientConfig,
    getClientConfigTargets,
    listSupportedClientIds,
    filterClientTargets,
    getLocalAppDataCacheDir,
    readGeneXusVersionFromInstall,
    readKbCatalog,
    addKbToConfig,
    removeKbFromConfig,
    switchActiveKb,
    applyLauncherConfigOrExit,
    isPathLikelyAppLockerBlocked,
    normalizeExePath,
    readClientCommandEntry
};
