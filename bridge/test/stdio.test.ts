import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const entryPoint = fileURLToPath(new URL('../src/index.ts', import.meta.url));

/**
 * Starts the real entry point as a child process, the same way an agent does, and connects to it
 * over stdio. Checks both protocol eras: 2025-era clients (`initialize` handshake, the default)
 * and 2026-07-28 clients (`versionNegotiation: 'auto'`).
 */
describe.each(['legacy', 'auto'] as const)('stdio (%s negotiation)', mode => {
    it('starts, lists tools and calls one', async () => {
        const client = new Client({ name: 'test-client', version: '1.0.0' }, { versionNegotiation: { mode } });
        await client.connect(
            new StdioClientTransport({ command: process.execPath, args: [entryPoint], stderr: 'ignore' })
        );

        try {
            expect(client.getProtocolEra()).toBe(mode === 'auto' ? 'modern' : 'legacy');

            const { tools } = await client.listTools();
            expect(tools.map(tool => tool.name)).toContain('bridge_status');

            const result = await client.callTool({ name: 'bridge_status' });
            expect(result.isError).toBeFalsy();
            expect(result.content).toEqual([{ type: 'text', text: expect.stringMatching(/^tidify-bridge .+ is running\.$/) }]);
        } finally {
            await client.close();
        }
    });
});
