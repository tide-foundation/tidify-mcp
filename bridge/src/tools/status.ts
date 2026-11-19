import type { McpServer } from '@modelcontextprotocol/server';
import { SERVER_NAME, SERVER_VERSION } from '../info.ts';

/**
 * The smallest useful tool: no arguments, returns text. Handy for checking that an agent is
 * connected.
 */
export function registerStatusTool(server: McpServer): void {
    server.registerTool(
        'bridge_status',
        {
            title: 'Bridge status',
            // The description is what the model reads to decide when to call the tool.
            description:
                'Reports that the tidify-bridge server is running and its version. ' +
                'Use this when the user asks whether the bridge is connected or working.',
            // Annotations are hints for the client (e.g. whether to ask before running).
            annotations: { readOnlyHint: true, idempotentHint: true, openWorldHint: false }
        },
        // A tool without inputSchema gets only the request context.
        async () => ({
            content: [{ type: 'text', text: `${SERVER_NAME} ${SERVER_VERSION} is running.` }]
        })
    );
}
