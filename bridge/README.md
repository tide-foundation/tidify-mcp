# tidify-bridge

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server written in TypeScript. AI
agents (Claude Code, VS Code Copilot, Claude Desktop, Cursor, ...) start it locally and talk to it
over stdio.

> **Status: template.** The server, tests and agent configuration work end to end. The tools are
> examples that show how the pieces fit:
>
> | Tool            | Shows                                                                   |
> | --------------- | ----------------------------------------------------------------------- |
> | `bridge_status` | the minimum: no arguments, text result                                  |
> | `calculate`     | typed input (Zod), structured output, error results                     |
> | `wait`          | the request context: progress notifications and cancellation           |
> | `open_google`   | acting on the user's machine: opens Google in the default browser      |

Built on the [MCP TypeScript SDK v2](https://ts.sdk.modelcontextprotocol.io/v2/)
(`@modelcontextprotocol/server`), which serves both 2025-era clients and clients on the 2026-07-28
protocol revision.

## Quick start

Requires Node.js 22.18 or later. It runs `.ts` files directly, so there is no build step during
development.

```bash
npm install
npm test
npm run inspect     # opens the MCP Inspector in your browser, connected to the server
```

**Use it from Claude Code.** [`.mcp.json`](.mcp.json) registers the server for Claude Code when
this folder is opened as the project. Approve `tidify-bridge` when asked, then ask
*"Is the bridge connected?"*. To add it to any other project:

```bash
claude mcp add tidify-bridge -- node /absolute/path/to/bridge/src/index.ts
```

**Other agents** take the same command. For example, VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
    "tidify-bridge": {
      "type": "stdio",
      "command": "node",
      "args": ["${workspaceFolder}/bridge/src/index.ts"]
    }
  }
}
```

## Scripts

| Script               | Does                                                            |
| -------------------- | --------------------------------------------------------------- |
| `npm start`          | runs the server on stdio (what an agent does)                   |
| `npm test`           | runs the tests once (`npm run test:watch` to keep watching)     |
| `npm run typecheck`  | type-checks `src/` and `test/`                                  |
| `npm run build`      | compiles `src/` to `dist/` (plain JavaScript, for publishing)   |
| `npm run inspect`    | starts the [MCP Inspector](https://github.com/modelcontextprotocol/inspector) web UI against the server, on protocol 2026-07-28 |

## Layout

```
bridge/
├── package.json
├── tsconfig.json          type-checking for src + test (no output)
├── tsconfig.build.json    emits src → dist
├── .mcp.json              Claude Code project config
├── src/
│   ├── index.ts           stdio entry point: serveStdio(createServer)
│   ├── server.ts          createServer(): builds the McpServer and registers every tool
│   ├── info.ts            server name, and the version read from package.json
│   └── tools/             one module per tool, each exporting register...Tool(server)
└── test/
    ├── tools.test.ts      calls each tool through a real MCP client, in memory
    └── stdio.test.ts      starts src/index.ts as a process, as an agent would
```

`server.ts` builds the server without a transport. `index.ts` serves it over stdio, and the tests
connect it to an in-memory client. This keeps tools testable without starting a process.

## Adding a tool

Create `src/tools/my-tool.ts`:

```ts
import type { McpServer } from '@modelcontextprotocol/server';
import * as z from 'zod/v4';

export function registerMyTool(server: McpServer): void {
    server.registerTool(
        'my_tool',
        {
            description: 'What it does, and when the agent should use it.',
            inputSchema: z.object({
                input: z.string().describe('What this argument means')
            })
        },
        async ({ input }, ctx) => {
            // ctx.mcpReq.signal aborts if the client cancels; pass it to fetch() etc.
            return { content: [{ type: 'text', text: `You said ${input}` }] };
        }
    );
}
```

Then call `registerMyTool(server)` in `src/server.ts`.

- Write `description` and `.describe()` text for the model. It's how the agent decides when and how
  to call the tool.
- Throw an `Error` for failures the agent should see. Its message is returned as a tool error
  (`isError: true`).
- **Never write to stdout** (no `console.log`). It carries the MCP protocol. Log with
  `console.error`, which goes to stderr.
- Import local files with the `.ts` extension (`./tools/my-tool.ts`). The build rewrites them to
  `.js`.
- Stick to TypeScript that Node can strip: no `enum`, `namespace`, or constructor parameter
  properties. The compiler flags these (`erasableSyntaxOnly`).

## Publishing

`npm pack` (or `npm publish`) builds `dist/` first. Users then run the server with
`npx tidify-bridge`. Before the first publish, remove `"private": true` from `package.json` and
confirm the package name is one you can own on npm.

## References

- [MCP TypeScript SDK v2 documentation](https://ts.sdk.modelcontextprotocol.io/v2/)
- [Tools guide](https://ts.sdk.modelcontextprotocol.io/v2/servers/tools)
- [MCP specification](https://modelcontextprotocol.io/specification)
