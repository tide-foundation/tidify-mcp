import { McpServer } from '@modelcontextprotocol/server';
import { SERVER_NAME, SERVER_VERSION } from './info.ts';
import { registerStatusTool } from './tools/status.ts';
import { registerWaitTool } from './tools/wait.ts';
import { registerLoginTool } from './tools/login.ts';

/**
 * Builds a server with every tool registered. It has no transport: the stdio entry point
 * (index.ts) and the tests each connect it to their own.
 *
 * To add a tool, create a module in tools/ that exports a `register...Tool(server)` function and
 * call it here.
 */
export function createServer(): McpServer {
    const server = new McpServer({ name: SERVER_NAME, version: SERVER_VERSION });

    registerStatusTool(server);
    registerWaitTool(server);
    registerLoginTool(server);

    return server;
}
