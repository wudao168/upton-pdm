import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { readBarcodes } from 'zxing-wasm/reader'
import jsQR from 'jsqr'
import { readProductionQrFrame, readProductionQrPhoto } from '../src/productionQr'
import { readLowQualityProductionQr } from '../src/productionQrEnhance'

vi.mock('zxing-wasm/reader', () => ({ prepareZXingModule: vi.fn(), readBarcodes: vi.fn() }))
vi.mock('jsqr', () => ({ default: vi.fn() }))
vi.mock('../src/productionQrEnhance', () => ({ readLowQualityProductionQr: vi.fn() }))
const draw = vi.fn()
beforeEach(() => {
  vi.mocked(readBarcodes).mockReset().mockResolvedValue([]); vi.mocked(jsQR).mockReset().mockReturnValue(null); draw.mockReset()
  vi.mocked(readLowQualityProductionQr).mockReset().mockResolvedValue(undefined)
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage: draw,
    getImageData: (_x: number, _y: number, width: number, height: number) => ({ data: new Uint8ClampedArray(4), width, height }) } as unknown as CanvasRenderingContext2D)
})
afterEach(() => vi.restoreAllMocks())
describe('生产二维码解码', () => {
  it('保留图号与版次的换行，正常识别不重复裁剪', async () => {
    vi.mocked(readBarcodes).mockResolvedValue([{ isValid: true, text: ' MODEL-01\nW1 ' }] as Awaited<ReturnType<typeof readBarcodes>>)
    expect(await readProductionQrFrame({} as CanvasImageSource, 1920, 1080)).toBe('MODEL-01\nW1')
    expect(draw).toHaveBeenCalledOnce(); expect(readBarcodes).toHaveBeenCalledWith(expect.objectContaining({ width: 1600, height: 900 }), expect.objectContaining({ formats: ['QRCode'], textMode: 'Plain' }))
  })
  it('全图失败后重试中央原始像素，再保留旧识别器兜底', async () => {
    vi.mocked(readBarcodes).mockResolvedValueOnce([]).mockResolvedValueOnce([{ isValid: true, text: 'UPLM-PRODUCTION|release|lot' }] as Awaited<ReturnType<typeof readBarcodes>>)
    expect(await readProductionQrFrame({} as CanvasImageSource, 1920, 1080)).toBe('UPLM-PRODUCTION|release|lot')
    expect(draw).toHaveBeenLastCalledWith(expect.anything(), 555, 135, 810, 810, 0, 0, 810, 810)
    expect(jsQR).not.toHaveBeenCalled()
    vi.mocked(jsQR).mockReturnValue({ data: 'FALLBACK\nA' } as ReturnType<typeof jsQR>)
    expect(await readProductionQrFrame({} as CanvasImageSource, 640, 480)).toBe('FALLBACK\nA')
  })
  it('没有相机帧时等待下一帧，空白或无二维码画面不产生定位内容', async () => {
    expect(await readProductionQrFrame({} as CanvasImageSource, 0, 0)).toBeUndefined()
    expect(draw).not.toHaveBeenCalled()
    expect(await readProductionQrFrame({} as CanvasImageSource, 640, 480)).toBeUndefined()
  })
  it('普通识别失败后自动增强，校验未通过的普通解码结果不用于定位', async () => {
    vi.mocked(readBarcodes).mockResolvedValue([{ isValid: false, text: '错误图号' }] as Awaited<ReturnType<typeof readBarcodes>>)
    vi.mocked(readLowQualityProductionQr).mockResolvedValueOnce('MODEL\nA')
    expect(await readProductionQrFrame({} as CanvasImageSource, 640, 480)).toBe('MODEL\nA')
    expect(jsQR).not.toHaveBeenCalled()
    expect(await readProductionQrFrame({} as CanvasImageSource, 640, 480)).toBeUndefined()
  })
  it('拒绝非图片及超过大小限制的照片，显示中文原因', async () => {
    await expect(readProductionQrPhoto(new File(['x'], 'file.txt', { type: 'text/plain' }))).rejects.toThrow('请选择图纸或批次流转卡的照片')
    const large = new File(['x'], 'photo.jpg', { type: 'image/jpeg' }); Object.defineProperty(large, 'size', { value: 21 * 1024 * 1024 })
    await expect(readProductionQrPhoto(large)).rejects.toThrow('照片超过20MB')
  })
})
