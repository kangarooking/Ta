import type { Context } from '@deepseek-ai/cordis'
import { defineTool } from '@deepseek-ai/dsh-tools'
import type { JSONValue, TaMethod } from '../contracts.js'
import type { TaToolRuntime } from '../runtime.js'
import {
  renderTaToolValue,
  requireFinite,
  requireInteger,
  requireMaxDimension,
  requirePositive,
  taToolOutputSchema,
} from './shared.js'

export function registerCaptureTool(ctx: Context, runtime: TaToolRuntime): void {
  ctx.tools.register(defineTool({
    name: 'ta_capture',
    description: 'Silently capture the foreground window, a display, a window (by exact ID, app name/Bundle ID, or title fragment), or known global region without activating Ta or moving the pointer.',
    parameters: {
      target: { type: 'string', enum: ['frontmost', 'display', 'window', 'region'], required: true },
      displayId: { type: 'integer' },
      windowId: { type: 'integer' },
      app: { type: 'string', description: 'For target=window: app name fragment or exact Bundle ID, instead of windowId.' },
      title: { type: 'string', description: 'For target=window: window-title fragment, alone or with app.' },
      maxDimension: { type: 'integer', description: 'Downscale so the longest side is at most this many pixels (e.g. 1568).' },
      x: { type: 'number' },
      y: { type: 'number' },
      width: { type: 'number' },
      height: { type: 'number' },
    },
    output: {
      schema: taToolOutputSchema,
      render: (_args, value) => renderTaToolValue(value),
    },
    async execute(args, exec) {
      let method: TaMethod
      let params: Record<string, JSONValue> = {}
      switch (args.target) {
        case 'frontmost':
          method = 'capture.frontmost'
          break
        case 'display':
          method = 'capture.display'
          if (args.displayId !== undefined) params.displayId = requireInteger(args.displayId, 'displayId')
          break
        case 'window':
          method = 'capture.window'
          if (args.windowId !== undefined) {
            params.windowId = requireInteger(args.windowId, 'windowId')
          } else {
            const app = args.app?.trim()
            const title = args.title?.trim()
            if (!app && !title) throw new Error('target=window requires windowId, app, or title.')
            if (app) params.app = app
            if (title) params.title = title
          }
          break
        case 'region':
          method = 'capture.region'
          params = {
            displayId: requireInteger(args.displayId, 'displayId'),
            x: requireFinite(args.x, 'x'),
            y: requireFinite(args.y, 'y'),
            width: requirePositive(args.width, 'width'),
            height: requirePositive(args.height, 'height'),
          }
          break
      }
      const maxDimension = requireMaxDimension(args.maxDimension)
      if (maxDimension !== undefined) params.maxDimension = maxDimension
      return runtime.call(method, params, exec.signal)
    },
  }))
}
