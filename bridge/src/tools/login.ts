import { acceptedContent, inputRequired, inputResponse, type McpServer } from '@modelcontextprotocol/server';
import z from 'zod';
import { once } from 'node:events';
import type { AddressInfo } from 'node:net';
import { openInBrowser } from '../browser.ts';
import { getVaultState, VaultState } from '../vault-management/vault-state.ts';

const url = process.env.TIDECLOAK_URL ?? 'http://localhost:8080';
const realm = process.env.TIDECLOAK_REALM ?? 'a';
const client = process.env.TIDECLOAK_CLIENT_ID ?? 'browser_login';

export function registerLoginTool(server: McpServer): void {
    server.registerTool(
        'login',
        {
            title: 'Tidecloak authentication',
            description: `Log into the "${realm}" Tidecloak realm hosted at ${url} through the "${client}" public browser client.`,
            annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: true }
        },
        async (ctx) => {
            const state = getVaultState();
            if (state === VaultState.NotRunning) throw new Error('The Asgard proxy is still starting. Try again in a few seconds.');

            switch(state){
                case VaultState.AdaptorRequired:
                    break;
                case VaultState.CertificateApprovalRequired:
                    break;
                case VaultState.Running:
                    break;
                default:
                    throw new Error("Unhandled state type: " + state);
            }

            try {
                const page = new URL(`http://127.0.0.1:${18433}/`);
                page.search = new URLSearchParams({ url, realm, clientId: client, fetchAdaptor: 'true' }).toString();
                await openInBrowser(page.href);
            }finally{

            }

            return { content: [{ type: 'text', text: `Logged into ${realm}.` }] };
        }
    )
}
