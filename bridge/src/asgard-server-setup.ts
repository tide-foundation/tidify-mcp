import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { SERVER_NAME } from './info.ts';

/** Where the vault listens (fixed in vault/Program.cs). The login tool opens the sign-in page here. */
export const VAULT_URL = 'http://127.0.0.1:18433/';

// bridge/src and bridge/dist both sit two levels below the repo root, next to vault/.
const VAULT_DIR = fileURLToPath(new URL('../../vault/', import.meta.url));
// Where `dotnet build` puts the app: Debug configuration, the csproj's TargetFramework.
const VAULT_DLL = 'bin/Debug/net10.0/Asgard.Vault.dll';
const browserClientId = process.env.TIDECLOAK_CLIENT_ID ?? 'browser_login';

let vault: ChildProcess | undefined;
let ready = false;

/** Whether the vault is running and answering. */
export function ensureServerReady(): boolean {
    return ready;
}

/**
 * Builds and starts the vault (vault/, an ASP.NET app) in the background, and stops it when the
 * bridge exits. Returns at once: ensureServerReady() turns true when the vault answers.
 */
export function startVault(): void {
    run().catch(error => console.error(`[${SERVER_NAME}] The vault failed to start:`, error));
}

async function run(): Promise<void> {
    // Output goes to our stderr (fd 2), never stdout: stdout carries the MCP protocol.
    const build = spawn('dotnet', ['build', '--nologo', '--verbosity', 'quiet'], { cwd: VAULT_DIR, stdio: ['ignore', 2, 2] });
    const [code] = await once(build, 'exit');
    if (code !== 0) throw new Error(`dotnet build exited with code ${code}.`);

    // The built app runs directly, not through `dotnet run`, so it's one process and kill() stops it
    // on every platform. cwd is its content root, where it serves wwwroot/ from.
    vault = spawn('dotnet', [
        VAULT_DLL,
        `--BrowserClientId=${browserClientId}`,
        `--Keycloak:auth-server-url=${process.env.TIDECLOAK_URL}`,
        `--Keycloak:realm=${process.env.TIDECLOAK_REALM}`
    ], {
        cwd: VAULT_DIR,
        stdio: ['ignore', 2, 2]
    });
    // Don't keep the bridge alive for it: the bridge still exits when the MCP client closes stdin.
    vault.unref();
    vault.once('error', error => console.error(`[${SERVER_NAME}] The vault failed to start:`, error));
    vault.once('exit', code => {
        ready = false;
        vault = undefined;
        console.error(`[${SERVER_NAME}] The vault exited (code ${code}).`);
    });
    // However the bridge exits (stdin closed, or SIGINT/SIGTERM in index.ts), stop the vault with it.
    process.once('exit', () => vault?.kill());

    while (vault && !ready) {
        try {
            await (await fetch(VAULT_URL)).text();
            ready = true;
            console.error(`[${SERVER_NAME}] The vault is listening on ${VAULT_URL}`);
        } catch {
            await delay(250, undefined, { ref: false });
        }
    }
}
