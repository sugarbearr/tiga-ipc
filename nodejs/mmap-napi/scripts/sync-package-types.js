const fs = require('fs');
const path = require('path');

const targetPath = path.resolve(__dirname, '..', 'index.d.ts');
const marker = 'export interface TigaServerContext {';
const extraTypes = `
export interface TigaServerContext {
  baseName: string
  clientId: string
  requestName: string
  responseName: string
  mappingDirectory: string
  requestId: string
  entryId: number
  mediaType?: string | null
}
export interface TigaServerOptions extends TigaChannelOptions {
  baseName: string
  discoveryIntervalMs?: number
  waitTimeoutMs?: number
  onInvoke(method: string, data: unknown, context: TigaServerContext): unknown | Promise<unknown>
  onError?(error: Error, context: Record<string, unknown>): void
}
export declare class TigaServer {
  constructor(options: TigaServerOptions)
  readonly baseName: string
  readonly mappingDirectory: string
  readonly closed: boolean
  readonly started: boolean
  start(): TigaServer
  close(): Promise<void>
}
export declare function createTigaServer(options: TigaServerOptions): TigaServer
export declare function startTigaServer(options: TigaServerOptions): TigaServer
`.trim();

const content = fs.readFileSync(targetPath, 'utf8');

if (content.includes(marker)) {
  process.exit(0);
}

fs.writeFileSync(targetPath, `${content.trimEnd()}\n${extraTypes}\n`);
