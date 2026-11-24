import type { McpServer } from '@modelcontextprotocol/server';
import { SERVER_NAME, SERVER_VERSION } from '../info.ts';
import { getVaultState, VaultState } from '../vault-management/vault-state.ts';

/**
 * The smallest useful tool: no arguments, returns text. Handy for checking that an agent is
 * connected.
 */
export function registerStatusTool(server: McpServer): void {
    server.registerTool(
        'bridge_status',
        {
            title: 'Bridge status',
            description:
                'Reports that the tidify-bridge server is running and its version. ' +
                'Use this when the user asks whether the mcp is connected or working.',
            // Annotations are hints for the client (e.g. whether to ask before running).
            annotations: { readOnlyHint: true, idempotentHint: true, openWorldHint: false }
        },
        // A tool without inputSchema gets only the request context.
        async () => ({
            content: [{ type: 'text', text: `${SERVER_NAME} ${SERVER_VERSION} is running.` }]
        })
    );
    server.registerTool(
        'vault_status',
        {
            title: 'Vault status',
            description:
                'Reports the internal service for interacting with the Tide Network is running and its version. ' +
                'Use this when the there was an error with accessing the encrypt/decryption/signing functionalities.',
            // Annotations are hints for the client (e.g. whether to ask before running).
            annotations: { readOnlyHint: true, idempotentHint: true, openWorldHint: false }
        },
        // A tool without inputSchema gets only the request context.
        async () => {
            const status = getVaultState();

            switch(status){
                case VaultState.NotRunning:
                    return { content: [{ type: 'text', text: `Vault v1 is not running. It may have crashed or not started yet.` }] }
                case VaultState.AdaptorRequired:
                    return { content: [{ type: 'text', text: `Vault v1 is running but the user must log in first for it to configure itself.` }] }
                case VaultState.CertificateApprovalRequired:
                    return { content: [{ type: 'text', text: `Vault v1 is running but the Tidecloak admin must first approve the resource certificate for this tool.` }] }
                case VaultState.Running:
                    return { content: [{ type: 'text', text: `Vault v1 is running and ready for tool calls.` }] }
                default:
                    throw Error("Unknown vault status: " + status);
            }
        }
    );
}
