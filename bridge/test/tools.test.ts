import { Client, InMemoryTransport } from '@modelcontextprotocol/client';
import type { CallToolResult } from '@modelcontextprotocol/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { openInBrowser } from '../src/browser.ts';
import { SERVER_VERSION } from '../src/info.ts';
import { createServer } from '../src/server.ts';

// Replace the real browser opener so tests never open a window.
vi.mock('../src/browser.ts', () => ({ openInBrowser: vi.fn(async () => {}) }));

/** Text of the first content block of a tool result. */
function text(result: CallToolResult): string | undefined {
    const [first] = result.content;
    return first?.type === 'text' ? first.text : undefined;
}

// Runs the real server in-process, connected to a real MCP client over an in-memory transport.
// No child process, so tests are fast and debuggable.
let client: Client;

beforeEach(async () => {
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await createServer().connect(serverTransport);
    client = new Client({ name: 'test-client', version: '1.0.0' });
    await client.connect(clientTransport);
});

afterEach(async () => {
    await client.close();
});

describe('server', () => {
    it('reports its name and version', () => {
        expect(client.getServerVersion()).toMatchObject({ name: 'tidify-bridge', version: SERVER_VERSION });
    });

    it('lists the example tools', async () => {
        const { tools } = await client.listTools();
        expect(tools.map(tool => tool.name).sort()).toEqual(['bridge_status', 'login', 'wait']);
    });

    it('publishes input schemas', async () => {
        const { tools } = await client.listTools();
        const wait = tools.find(tool => tool.name === 'wait');
        expect(wait?.inputSchema.required).toEqual(['seconds']);
    });
});

describe('bridge_status', () => {
    it('reports that the server is running', async () => {
        const result = await client.callTool({ name: 'bridge_status' });
        expect(result.isError).toBeFalsy();
        expect(text(result)).toBe(`tidify-bridge ${SERVER_VERSION} is running.`);
    });
});

describe('login', () => {
    /** Whether anything still answers at url. */
    async function listening(url: string): Promise<boolean> {
        try {
            await (await fetch(url)).text();
            return true;
        } catch {
            return false;
        }
    }

    it('serves the sign-in page until the page reports it is done, then closes it', async () => {
        let pageUrl = '';
        let html = '';
        // Stands in for the browser: load the page, then, once sign-in has finished, POST /done.
        vi.mocked(openInBrowser).mockImplementationOnce(async url => {
            pageUrl = url;
            html = await (await fetch(url)).text();
            await fetch(new URL('/done', url), { method: 'POST' });
        });

        const result = await client.callTool({ name: 'login' });

        expect(result.isError).toBeFalsy();
        expect(text(result)).toBe('Logged into a.');
        const page = new URL(pageUrl);
        expect(page.origin).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/);
        expect(page.pathname).toBe('/');
        expect(Object.fromEntries(page.searchParams)).toEqual({ url: 'http://localhost:8080', realm: 'a', clientId: 'browser_login' });
        expect(html).toContain('<script type="module" src="/login.js"></script>');
        expect(await listening(pageUrl)).toBe(false);
    });

    it('closes the server when the browser cannot be opened', async () => {
        let pageUrl = '';
        vi.mocked(openInBrowser).mockImplementationOnce(async url => {
            pageUrl = url;
            throw new Error('spawn xdg-open ENOENT');
        });

        const result = await client.callTool({ name: 'login' });

        expect(result.isError).toBe(true);
        expect(text(result)).toContain('spawn xdg-open ENOENT');
        expect(await listening(pageUrl)).toBe(false);
    });

    it('closes the server when the client cancels', async () => {
        let pageUrl = '';
        // The browser opens, but nobody signs in.
        vi.mocked(openInBrowser).mockImplementationOnce(async url => {
            pageUrl = url;
        });

        await expect(client.callTool({ name: 'login' }, { signal: AbortSignal.timeout(100) })).rejects.toThrow();
        await vi.waitFor(async () => expect(await listening(pageUrl)).toBe(false));
    });
});

describe('wait', () => {
    it('reports progress', async () => {
        const progress: number[] = [];
        const result = await client.callTool(
            { name: 'wait', arguments: { seconds: 1 } },
            { onprogress: update => progress.push(update.progress) }
        );
        expect(text(result)).toBe('Waited 1 seconds.');
        expect(progress).toEqual([1]);
    });

    it('stops when the client cancels', async () => {
        const call = client.callTool({ name: 'wait', arguments: { seconds: 30 } }, { signal: AbortSignal.timeout(100) });
        await expect(call).rejects.toThrow();
    });
});
