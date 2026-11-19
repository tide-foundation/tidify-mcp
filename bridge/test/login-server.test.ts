import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { DPOP_AUTH_HTML, dpopAuthPathFor } from '@tidecloak/js/dpop-server';
import { afterEach, describe, expect, it } from 'vitest';
import { startLoginServer, type TidecloakConfig } from '../src/login-server.ts';

const config: TidecloakConfig = { url: 'http://localhost:8080', realm: 'a', clientId: 'browser_login' };

const servers: Server[] = [];

/** Starts a login server on a free port, so tests never collide with the default one. */
async function start(overrides: Partial<TidecloakConfig> = {}): Promise<{ server: Server; base: string }> {
    const server = await startLoginServer({ ...config, ...overrides }, 0);
    servers.push(server);
    return { server, base: `http://127.0.0.1:${(server.address() as AddressInfo).port}` };
}

afterEach(() => {
    for (const server of servers.splice(0)) {
        server.close();
        server.closeAllConnections();
    }
});

/** The config the page reads from its <script id="config"> element. */
function pageConfig(html: string): unknown {
    return JSON.parse(/<script id="config" type="application\/json">(.*?)<\/script>/s.exec(html)?.[1] ?? 'null');
}

describe('login server', () => {
    it('listens on 127.0.0.1 only', async () => {
        const { server } = await start();
        expect((server.address() as AddressInfo).address).toBe('127.0.0.1');
    });

    it('serves the page with the Tidecloak config', async () => {
        const { base } = await start();
        const response = await fetch(`${base}/`);
        expect(response.status).toBe(200);
        expect(response.headers.get('content-type')).toBe('text/html; charset=utf-8');
        const html = await response.text();
        expect(pageConfig(html)).toEqual(config);
        expect(html).toContain('<script type="module" src="/login.js"></script>');
    });

    it('escapes "<" in the config so a value cannot close the script element', async () => {
        const realm = '</script><script>alert(1)</script>';
        const { base } = await start({ realm });
        const html = await (await fetch(`${base}/`)).text();
        expect(html).not.toContain(realm);
        expect(pageConfig(html)).toMatchObject({ realm });
    });

    it('serves the bundled page script', async () => {
        const { base } = await start();
        const response = await fetch(`${base}/login.js`);
        expect(response.status).toBe(200);
        expect(response.headers.get('content-type')).toBe('text/javascript');
        expect(await response.text()).toBe(readFileSync(new URL('../page/dist/login.js', import.meta.url), 'utf8'));
    });

    it('serves tide_dpop_auth.html at the path for the configured issuer and client', async () => {
        const { base } = await start();
        const response = await fetch(base + dpopAuthPathFor('http://localhost:8080/realms/a', 'browser_login'));
        expect(response.status).toBe(200);
        expect(response.headers.get('content-security-policy')).toContain("script-src 'self' 'sha256-");
        expect(response.headers.get('allow-csp-from')).toBe('*');
        expect(await response.text()).toBe(DPOP_AUTH_HTML);
    });

    it('ignores a trailing slash on the Tidecloak URL when building the issuer', async () => {
        const { base } = await start({ url: 'http://localhost:8080/' });
        const response = await fetch(base + dpopAuthPathFor('http://localhost:8080/realms/a', 'browser_login'));
        await response.text();
        expect(response.status).toBe(200);
    });

    it.each([
        ['another realm', 'http://localhost:8080/realms/b', 'browser_login'],
        ['another Tidecloak', 'http://tidecloak.test/realms/a', 'browser_login'],
        ['another client', 'http://localhost:8080/realms/a', 'other_client']
    ])('refuses tide_dpop_auth.html for %s', async (_, issuer, clientId) => {
        const { base } = await start();
        const response = await fetch(base + dpopAuthPathFor(issuer, clientId));
        await response.text();
        expect(response.status).toBe(403);
    });

    it('returns 404 for anything else', async () => {
        const { base } = await start();
        const response = await fetch(`${base}/tidecloak.json`);
        await response.text();
        expect(response.status).toBe(404);
    });

    it('closes itself when the page reports that sign-in has finished', async () => {
        const { server, base } = await start();
        const closed = once(server, 'close');
        const response = await fetch(`${base}/done`, { method: 'POST' });
        expect(response.status).toBe(204);
        await closed;
        await expect(fetch(`${base}/`)).rejects.toThrow();
    });

    it('rejects when the port is already in use', async () => {
        const { server } = await start();
        const { port } = server.address() as AddressInfo;
        await expect(startLoginServer(config, port)).rejects.toMatchObject({ code: 'EADDRINUSE' });
    });
});
