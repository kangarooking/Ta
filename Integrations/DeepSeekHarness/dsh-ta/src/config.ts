import { homedir } from 'node:os'
import { join } from 'node:path'
import Schema from '@deepseek-ai/schemastery'

export interface Config {
  socketPath: string
  appPath: string
  timeoutMs: number
  autoLaunch: boolean
  /**
   * 'inherit' (default) sends no cloud parameter, so the policy the user chose in
   * Ta applies. An explicit value can only make a request stricter than that
   * setting; Ta never lets a caller loosen it.
   */
  defaultCloud: 'inherit' | 'auto' | 'allow' | 'deny'
  allowClipboard: boolean
}

export const defaultConfig: Config = {
  socketPath: join(homedir(), 'Library', 'Application Support', 'Ta', 'agent-v1.sock'),
  appPath: '/Applications/拓.app',
  timeoutMs: 15_000,
  autoLaunch: true,
  defaultCloud: 'inherit',
  allowClipboard: false,
}

export const Config: Schema<Config> = Schema.object({
  socketPath: Schema.string().default(defaultConfig.socketPath),
  appPath: Schema.string().default(defaultConfig.appPath),
  timeoutMs: Schema.number().min(100).max(120_000).default(defaultConfig.timeoutMs),
  autoLaunch: Schema.boolean().default(defaultConfig.autoLaunch),
  defaultCloud: Schema.union(['inherit', 'auto', 'allow', 'deny']).default(defaultConfig.defaultCloud),
  allowClipboard: Schema.boolean().default(defaultConfig.allowClipboard),
})
