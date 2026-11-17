import type { Context } from '@deepseek-ai/cordis'
import { defineTool } from '@deepseek-ai/dsh-tools'
import { isAbsolute } from 'node:path'
import type { Config } from '../config.js'
import type { JSONValue } from '../contracts.js'
import type { TaToolRuntime } from '../runtime.js'
import { renderTaToolValue, requireText, taToolOutputSchema } from './shared.js'

export function registerDeliverTool(ctx: Context, runtime: TaToolRuntime, config: Config): void {
  ctx.tools.register(defineTool({
    name: 'ta_deliver',
    description: 'Explicitly save a Ta image to an absolute path or copy text/image to the macOS clipboard. Clipboard access must be enabled in plugin config.',
    parameters: {
      action: { type: 'string', enum: ['save', 'copyText', 'copyImage'], required: true },
      artifactId: { type: 'string', description: 'Ta artifact ID; omit for the latest image.' },
      path: { type: 'string', description: 'Required absolute .png destination for save. Hidden paths, ~/Library and system directories are refused.' },
      overwrite: { type: 'boolean', description: 'Allow save to replace an existing file. Defaults to false.' },
      text: { type: 'string', description: 'Required content for copyText.' },
    },
    output: {
      schema: taToolOutputSchema,
      render: (_args, value) => renderTaToolValue(value),
    },
    async execute(args, exec) {
      if (args.action === 'save') {
        const path = requireText(args.path, 'path')
        if (!isAbsolute(path)) throw new Error('path must be absolute.')
        const params: Record<string, JSONValue> = {
          ...runtime.imageParams(args.artifactId),
          path,
          ...(args.overwrite === true ? { overwrite: true } : {}),
        }
        return runtime.call('deliver.save', params, exec.signal)
      }
      if (!config.allowClipboard) {
        throw new Error('Clipboard delivery is disabled. Set allowClipboard: true in the dsh-ta plugin config.')
      }
      if (args.action === 'copyText') {
        return runtime.call('deliver.copy', { text: requireText(args.text, 'text') }, exec.signal)
      }
      return runtime.call('deliver.copy', runtime.imageParams(args.artifactId), exec.signal)
    },
  }))
}
