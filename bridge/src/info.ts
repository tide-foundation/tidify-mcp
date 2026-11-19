import { createRequire } from 'node:module';

export const SERVER_NAME = 'tidify-bridge';

// package.json is one level up from both src/ and dist/, so this works from source and when built.
export const SERVER_VERSION: string = (
    createRequire(import.meta.url)('../package.json') as { version: string }
).version;
