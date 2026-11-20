#!/usr/bin/env node
/**
 * stdio entry point. The MCP client (Claude Code, VS Code, Claude Desktop, ...) starts this
 * process and talks JSON-RPC over stdin/stdout.
 *
 * stdout carries the protocol: never write to it (no console.log). Log with console.error,
 * which goes to stderr.
 */
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { SERVER_NAME, SERVER_VERSION } from './info.ts';
import { createServer } from './server.ts';
import { startVault } from './vault-management/asgard-server-setup.ts';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';

// TEMP: persistence test
if (existsSync('hello.txt')) console.error(`[${SERVER_NAME}] hello.txt: ${readFileSync('hello.txt', 'utf8')}`);
else writeFileSync('hello.txt', `hello ${new Date().toISOString()}`);




// ensure asgard sever is set up first
// Builds and starts the vault in the background, so a slow build can't time out the MCP client's
// startup. Until it answers, tools that need it get "still starting" (ensureServerReady()).
startVault();

// serveStdio takes a factory rather than a server instance so it can serve both protocol eras
// (2025-era `initialize` clients and 2026-07-28 clients) from the same tool definitions.
const handle = serveStdio(createServer, {
    onerror: error => console.error(`[${SERVER_NAME}]`, error)
});

console.error(`[${SERVER_NAME}] ${SERVER_VERSION} running on stdio`);

// The process exits on its own when the client closes stdin. Signals need an explicit shutdown.
for (const signal of ['SIGINT', 'SIGTERM'] as const) {
    process.once(signal, () => {
        void handle.close().finally(() => process.exit(0));
    });
}
