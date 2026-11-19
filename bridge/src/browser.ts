import { spawn } from 'node:child_process';
import { release } from 'node:os';

/**
 * Opens a URL with Windows' default handler for it. Not explorer.exe: it mangles URLs with a query
 * string (& and %-escapes) and opens File Explorer instead.
 */
const WINDOWS_OPENER: [command: string, args: string[]] = ['rundll32.exe', ['url.dll,FileProtocolHandler']];

/** The command that opens a URL in the default browser on this machine. */
function browserCommand(url: string): [command: string, args: string[]] {
    switch (process.platform) {
        case 'darwin':
            return ['open', [url]];
        case 'win32':
            return [WINDOWS_OPENER[0], [...WINDOWS_OPENER[1], url]];
        default:
            // Under WSL, hand the URL to Windows, where the user's browser actually is.
            return release().toLowerCase().includes('microsoft')
                ? [WINDOWS_OPENER[0], [...WINDOWS_OPENER[1], url]]
                : ['xdg-open', [url]];
    }
}

/**
 * Opens a URL in the user's default browser. Resolves once the opener has started; rejects if it
 * can't be started (e.g. the command isn't installed).
 */
export function openInBrowser(url: string): Promise<void> {
    const [command, args] = browserCommand(url);

    return new Promise((resolve, reject) => {
        // stdio: 'ignore' matters: anything the child printed to our stdout would corrupt the
        // MCP stream. detached + unref lets the browser outlive the server.
        const child = spawn(command, args, { stdio: 'ignore', detached: true });
        child.once('error', reject);
        child.once('spawn', () => {
            child.unref();
            resolve();
        });
    });
}
