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
    const possible = [
        'C:\\Program Files (x86)\\GeneXus\\GeneXus18',
        'C:\\Program Files (x86)\\GeneXus\\GeneXus17',
        'C:\\Program Files (x86)\\GeneXus\\GeneXus16',
        'C:\\Program Files\\GeneXus\\GeneXus18',
        'C:\\Program Files\\GeneXus\\GeneXus17'
    ];

    for (const candidate of possible) {
        if (fs.existsSync(path.join(candidate, 'genexus.exe'))) {
            return candidate;
        }
    }

    const fromRegistry = discoverGeneXusFromRegistry();
    if (fromRegistry) return fromRegistry;

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

function patchClientConfig(targetConfigPath) {
    const clients = getClientConfigTargets();

    // Set by scripts/install.ps1 for fixed-path corporate installs — clients
    // launch the gateway exe directly instead of resolving via the npx cache.
    const directExe = process.env.GENEXUS_MCP_GATEWAY_EXE;
    const launcher = directExe
        ? { command: directExe, args: [] }
        : { command: process.platform === 'win32' ? 'npx.cmd' : 'npx', args: ['-y', 'genexus-mcp@latest'] };

    const patched = [];
    const failed = [];

    for (const client of clients) {
        if (!fs.existsSync(client.path)) continue;

        try {
            const parsed = readJsonFileSafe(client.path);
            if (parsed === null) {
                failed.push({ client: client.name, reason: 'Invalid JSON' });
                continue;
            }

            const cfgObj = parsed || {};
            cfgObj.mcpServers = cfgObj.mcpServers || {};
            cfgObj.mcpServers.genexus = { ...launcher, env: { GX_CONFIG_PATH: targetConfigPath } };

            fs.writeFileSync(client.path, JSON.stringify(cfgObj, null, 2));
            patched.push(client.name);
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { patched, failed };
}

function getClientConfigTargets() {
    return [
        { path: path.join(os.homedir(), 'AppData', 'Roaming', 'Claude', 'claude_desktop_config.json'), name: 'Claude Desktop (Windows)' },
        { path: path.join(os.homedir(), 'Library', 'Application Support', 'Claude', 'claude_desktop_config.json'), name: 'Claude Desktop (macOS)' },
        { path: path.join(os.homedir(), '.gemini', 'antigravity', 'mcp_config.json'), name: 'Antigravity' },
        { path: path.join(os.homedir(), '.claude.json'), name: 'Claude Code' }
    ];
}

function unpatchClientConfig() {
    const clients = getClientConfigTargets();
    const removed = [];
    const skipped = [];
    const failed = [];

    for (const client of clients) {
        if (!fs.existsSync(client.path)) continue;

        try {
            const parsed = readJsonFileSafe(client.path);
            if (parsed === null) {
                failed.push({ client: client.name, reason: 'Invalid JSON' });
                continue;
            }

            const cfgObj = parsed || {};
            if (!cfgObj.mcpServers || !cfgObj.mcpServers.genexus) {
                skipped.push({ client: client.name, reason: 'no genexus entry' });
                continue;
            }

            delete cfgObj.mcpServers.genexus;
            fs.writeFileSync(client.path, JSON.stringify(cfgObj, null, 2));
            removed.push(client.name);
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { removed, skipped, failed };
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
    getLocalAppDataCacheDir,
    readGeneXusVersionFromInstall,
    readKbCatalog,
    addKbToConfig,
    removeKbFromConfig,
    switchActiveKb,
    applyLauncherConfigOrExit
};
