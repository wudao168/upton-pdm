import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { inflateSync } from 'node:zlib'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { prepareZXingModule, readBarcodes } from 'zxing-wasm/reader'
import { readLowQualityProductionQr } from '../src/productionQrEnhance'
import { screenQrPixels } from './productionQrScreenFixture'

const originalImageData = globalThis.ImageData
beforeAll(() => {
  globalThis.ImageData = class {
    data: Uint8ClampedArray
    constructor(public width: number, public height: number) { this.data = new Uint8ClampedArray(width * height * 4) }
  } as typeof ImageData
  const require = createRequire(import.meta.url)
  prepareZXingModule({ overrides: { wasmBinary: readFileSync(require.resolve('zxing-wasm/reader/zxing_reader.wasm')) } })
})
afterAll(() => { globalThis.ImageData = originalImageData })
function sample() {
  const image = new ImageData(screenQrPixels.width, screenQrPixels.height)
  image.data.set(inflateSync(Buffer.from(screenQrPixels.rgbaDeflate, 'base64')))
  return image
}
describe('真实低画质二维码回归（实际解码器）', () => {
  it('普通识别失败的屏幕拍摄样本，经增强读出准确图号和版本', async () => {
    const image = sample()
    expect(await readBarcodes(image, { formats: ['QRCode'], tryHarder: true })).toEqual([])
    expect(await readLowQualityProductionQr(image)).toBe('P700022.10.00-02\nA')
  })
  it('二维码旋转后仍通过校验，不依赖样本的固定方向', async () => {
    const image = sample(), rotated = new ImageData(image.height, image.width)
    for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++)
      rotated.data.set(image.data.subarray((y * image.width + x) * 4, (y * image.width + x) * 4 + 4), (x * image.height + image.height - 1 - y) * 4)
    expect(await readLowQualityProductionQr(rotated)).toBe('P700022.10.00-02\nA')
  })
  it('二维码位于大画面边缘时自动定位其区域，空白画面不产生内容', async () => {
    const image = sample(), frame = new ImageData(600, 900); frame.data.fill(255)
    for (let y = 0; y < image.height; y++) frame.data.set(image.data.subarray(y * image.width * 4, (y + 1) * image.width * 4), ((y + 80) * frame.width + 390) * 4)
    expect(await readLowQualityProductionQr(frame)).toBe('P700022.10.00-02\nA')
    frame.data.fill(255); expect(await readLowQualityProductionQr(frame)).toBeUndefined()
  })
})
