import { afterEach, describe, expect, it, vi } from 'vitest'
import { downloadProductionDrawingFile, PdmApiError } from '../src/api'
import type { ProductionDrawingItem } from '../src/types'

const drawing = { documentId: 'drawing', versionId: 'released-drawing', drawingNumber: 'DRW-001' } as ProductionDrawingItem

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  delete (window as Window & { showSaveFilePicker?: unknown }).showSaveFilePicker
})

function savePicker() {
  const write = vi.fn().mockResolvedValue(undefined)
  const close = vi.fn().mockResolvedValue(undefined)
  const createWritable = vi.fn().mockResolvedValue({ write, close })
  Object.defineProperty(window, 'showSaveFilePicker', {
    configurable: true, value: vi.fn().mockResolvedValue({ createWritable }),
  })
  return { createWritable, write, close }
}

describe('生产图纸专用下载', () => {
  it('PDF 每次经生产有效性接口读取，并禁止使用缓存', async () => {
    const saved = savePicker()
    const request = vi.fn().mockResolvedValue(new Response('PDF', { status: 200 }))
    vi.stubGlobal('fetch', request)
    await downloadProductionDrawingFile(drawing, 'Pdf', 'token')
    expect(request).toHaveBeenCalledWith(expect.stringContaining('/production-drawings/drawing/versions/released-drawing/preview'),
      expect.objectContaining({ cache: 'no-store' }))
    expect(request.mock.calls[0]![0]).toContain('releasePackageId=')
    expect(saved.write).toHaveBeenCalledOnce()
    expect(saved.close).toHaveBeenCalledOnce()
  })

  it('STEP 携带所属图纸和模型正式版本，供服务端核对同包关系', async () => {
    savePicker()
    const request = vi.fn().mockResolvedValue(new Response('STEP', { status: 200 }))
    vi.stubGlobal('fetch', request)
    await downloadProductionDrawingFile(drawing, 'Step', 'token', undefined,
      { documentId: 'model', versionId: 'released-model', drawingNumber: 'MODEL-001' })
    expect(request.mock.calls[0]![0]).toContain('/production-drawings/drawing/versions/released-drawing/preview?')
    expect(request.mock.calls[0]![0]).toContain('modelDocumentId=model&modelVersionId=released-model')
  })

  it('服务端发现图纸已作废时保留中文原因且不保存文件', async () => {
    const saved = savePicker()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: '图纸已作废，不能用于生产。' }), { status: 409 })))
    await expect(downloadProductionDrawingFile(drawing, 'Pdf', 'token')).rejects.toBeInstanceOf(PdmApiError)
    expect(saved.createWritable).not.toHaveBeenCalled()
  })
})
