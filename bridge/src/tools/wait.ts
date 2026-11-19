import type { McpServer } from '@modelcontextprotocol/server';
import { setTimeout as delay } from 'node:timers/promises';
import * as z from 'zod/v4';

/**
 * Shows the request context (`ctx`), the second handler argument, for long-running work:
 *
 * - Progress: if the client asked for progress (it sent a progressToken), send
 *   `notifications/progress` as the work advances.
 * - Cancellation: `ctx.mcpReq.signal` aborts when the client cancels or disconnects. Pass it to
 *   anything that accepts an AbortSignal (fetch, timers, child processes, ...).
 */
export function registerWaitTool(server: McpServer): void {
    server.registerTool(
        'wait',
        {
            title: 'Wait',
            description:
                'Waits for the given number of seconds, reporting progress once per second. ' +
                'Only for testing progress notifications and cancellation.',
            inputSchema: z.object({
                seconds: z.number().int().min(1).max(30).describe('How long to wait, in seconds (1-30)')
            }),
            annotations: { readOnlyHint: true, openWorldHint: false }
        },
        async ({ seconds }, ctx) => {
            const progressToken = ctx.mcpReq._meta?.progressToken;

            for (let elapsed = 1; elapsed <= seconds; elapsed++) {
                // Rejects with an AbortError if the request is cancelled.
                await delay(1000, undefined, { signal: ctx.mcpReq.signal });

                if (progressToken !== undefined) {
                    await ctx.mcpReq.notify({
                        method: 'notifications/progress',
                        params: { progressToken, progress: elapsed, total: seconds, message: `${elapsed}s elapsed` }
                    });
                }
            }

            return { content: [{ type: 'text', text: `Waited ${seconds} seconds.` }] };
        }
    );
}
