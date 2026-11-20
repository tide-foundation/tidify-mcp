import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import type { Socket } from 'node:net';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { SERVER_NAME } from '../info.ts';
import { getVaultState, parseVaultState, setVaultState, VaultState } from './vault-state.ts';

/** Where the vault listens (fixed in vault/Program.cs). The login tool opens the sign-in page here. */
export const VAULT_URL = 'http://127.0.0.1:18433/';

// bridge/src and bridge/dist both sit two levels below the repo root, next to vault/.
const VAULT_DIR = fileURLToPath(new URL('../../../vault/', import.meta.url));
// Where `dotnet build` puts the app: Debug configuration, the csproj's TargetFramework.
const VAULT_DLL = 'bin/Debug/net10.0/Asgard.Vault.dll';
/** The line vault/Program.cs prints on stdout once it has bound VAULT_URL's port. */
const STATUS_MARKER = 'VAULT_STATUS';
/** The message vault/Program.cs throws when it has no Keycloak config (no appsettings.json yet). */
const MISSING_CONFIG = 'No Keycloak realm configuration found.';
/** The exit code vault/Program.cs uses to ask for a restart (after /adaptor writes appsettings.json). */
const RESTART_EXIT_CODE = 3;
const browserClientId = process.env.TIDECLOAK_CLIENT_ID ?? 'browser_login';

let vault: ChildProcess | undefined;


/**
 * Builds and starts the vault (vault/, an ASP.NET app) in the background, and stops it when the
 * bridge exits. Returns at once: ensureServerReady() turns true when the vault reports it is listening.
 */
export function startVault(): void {
    run().catch(error => console.error(`[${SERVER_NAME}] The vault failed to start:`, error));
}

async function run(): Promise<void> {
    // Output goes to our stderr (fd 2), never stdout: stdout carries the MCP protocol.
    const build = spawn('dotnet', ['build', '--nologo', '--verbosity', 'quiet'], { cwd: VAULT_DIR, stdio: ['ignore', 2, 2] });
    const [code] = await once(build, 'exit');
    if (code !== 0) throw new Error(`dotnet build exited with code ${code}.`);

    // However the bridge exits (stdin closed, or SIGINT/SIGTERM in index.ts), stop the vault with it.
    process.once('exit', () => vault?.kill());
    launch(false);
}

/**
 * Starts the vault with appsettings.json as its config. If it has none yet (vault/Program.cs throws
 * MISSING_CONFIG), relaunches it once with the Keycloak values from our environment.
 */
function launch(withDefaults: boolean): void {
    // The built app runs directly, not through `dotnet run`, so it's one process and kill() stops it
    // on every platform. cwd is its content root, where it serves wwwroot/ from.
    vault = spawn('dotnet', [
        VAULT_DLL,
        `--BrowserClientId=${browserClientId}`,
        ...(withDefaults ? [
            `--Keycloak:auth-server-url=${process.env.TIDECLOAK_URL}`,
            `--Keycloak:realm=${process.env.TIDECLOAK_REALM}`
        ] : [])
    ], {
        cwd: VAULT_DIR,
        // stdout is piped so we can spot STATUS_MARKER, stderr so we can spot MISSING_CONFIG. Both are
        // forwarded to our stderr, never our stdout: stdout carries the MCP protocol.
        stdio: ['ignore', 'pipe', 'pipe']
    });
    // Don't keep the bridge alive for it: the bridge still exits when the MCP client closes stdin.
    // The pipes are handles of their own, so they need unref() too.
    vault.unref();
    (vault.stdout as Socket).unref();
    (vault.stderr as Socket).unref();

    let missingConfig = false;
    createInterface({ input: vault.stderr! }).on('line', line => {
        if (line.includes(MISSING_CONFIG)) missingConfig = true;
        process.stderr.write(line + '\n');
    });

    // Readiness comes from the vault itself, not from probing the port: if another service already
    // holds 18433, a probe would get that service's answer, while the vault fails to bind and exits.
    createInterface({ input: vault.stdout! }).on('line', line => {
        if (getVaultState() === VaultState.NotRunning && line.startsWith(STATUS_MARKER)) {
            try {
                console.error(line.substring(STATUS_MARKER.length + 1))
                setVaultState(parseVaultState(line.substring(STATUS_MARKER.length + 1)));
                console.error(`[${SERVER_NAME}] The vault is listening on ${VAULT_URL}`);
            } catch (error) {
                console.error(`[${SERVER_NAME}] Bad vault status line "${line}":`, error);
            }
        } else {
            process.stderr.write(line + '\n');
        }
    });
    vault.once('error', error => console.error(`[${SERVER_NAME}] The vault failed to start:`, error));
    vault.once('exit', code => {
        const wasListening = getVaultState() !== VaultState.NotRunning;
        setVaultState(VaultState.NotRunning);
        vault = undefined;
        if (code === RESTART_EXIT_CODE) {
            console.error(`[${SERVER_NAME}] The vault asked to restart.`);
            launch(false);
            return;
        }
        if (missingConfig && !withDefaults) {
            console.error(`[${SERVER_NAME}] The vault has no appsettings.json yet. Retrying with the default Keycloak values.`);
            launch(true);
            return;
        }
        // Exiting before STATUS_MARKER usually means the port was taken (Kestrel logs the bind error above).
        console.error(`[${SERVER_NAME}] The vault exited (code ${code})${wasListening ? '' : ' before it was listening'}.`);
    });
}
