import type { Context } from '@deepseek-ai/cordis'
import { defineTool } from '@deepseek-ai/dsh-tools'
import type { Config } from '../config.js'
import type { JSONValue } from '../contracts.js'
import type { TaToolRuntime } from '../runtime.js'
import { cloudParams, dataString, renderTaToolValue, taToolOutputSchema } from './shared.js'

export function registerTranslateTool(ctx: Context, runtime: TaToolRuntime, config: Config): void {
  ctx.tools.register(defineTool({
    name: 'ta_translate',
    description: 'Translate supplied text, OCR text from a Ta artifact, or render an image translation using the translation profile configured in Ta.',
    parameters: {
      mode: { type: 'string', enum: ['text', 'image'], required: true },
      text: { type: 'string', description: 'Text to translate directly. Omit to OCR the selected artifact first.' },
      artifactId: { type: 'string', description: 'Ta artifact ID returned by ta_capture; omit for the latest artifact.' },
      cloud: {
        type: 'string', enum: ['auto', 'allow', 'deny'],
        description: 'Omit to use the policy configured in Ta. Can only be stricter than that setting.',
      },
    },
    output: {
      schema: taToolOutputSchema,
      render: (_args, value) => renderTaToolValue(value),
    },
    async execute(args, exec) {
      const cloud = cloudParams(args.cloud, config.defaultCloud)
      if (args.mode === 'image') {
        const params: Record<string, JSONValue> = { ...runtime.imageParams(args.artifactId), ...cloud }
        return runtime.call('translate.image', params, exec.signal)
      }
      let text = args.text
      if (text === undefined) {
        const ocr = await runtime.call(
          'recognize.ocr',
          { ...runtime.imageParams(args.artifactId), ...cloud },
          exec.signal,
        )
        text = dataString(ocr.data, 'text')
      }
      if (text === undefined || text.trim().length === 0) {
        throw new Error('Ta OCR returned no text to translate.')
      }
      return runtime.call('translate.text', { text, ...cloud }, exec.signal)
    },
  }))
}
