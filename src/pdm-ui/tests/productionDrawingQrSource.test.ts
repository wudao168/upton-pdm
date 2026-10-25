import { beforeEach, describe, expect, it, vi } from 'vitest'
import { writeBarcode } from 'zxing-wasm/writer'
import { createProductionDrawingQr } from '../src/productionDrawingQr'

vi.mock('zxing-wasm/writer', () => ({ prepareZXingModule: vi.fn(), writeBarcode: vi.fn() }))
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(writeBarcode).mockResolvedValue({ svg: '<svg></svg>', error: '' } as Awaited<ReturnType<typeof writeBarcode>>)
})
describe('用服务端共用规则生成的二维码数据直接画码', () => {
  it('完整保留服务端编码内容和正式版次，不读取PDF', async () => {
    const content = 'P700022.10.00-07\nA'
    const result = await createProductionDrawingQr(content)
    expect(result.content).toBe(content)
    expect(writeBarcode).toHaveBeenCalledWith(content, expect.objectContaining({ options: 'ecLevel=M', addQuietZones: true }))
  })
  it('数据缺失时不猜测版次', async () => {
    await expect(createProductionDrawingQr(null)).rejects.toThrow('缺少正式二维码数据')
    expect(writeBarcode).not.toHaveBeenCalled()
  })
  it('生成失败时只显示中文原因', async () => {
    vi.mocked(writeBarcode).mockResolvedValue({ error: 'writer failed', svg: '' } as Awaited<ReturnType<typeof writeBarcode>>)
    await expect(createProductionDrawingQr('DRAWING\nB')).rejects.toThrow('图纸二维码生成失败，请重试。')
  })
})