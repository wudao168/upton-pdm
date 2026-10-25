import jsQR from 'jsqr'
import { prepareZXingModule, readBarcodes } from 'zxing-wasm/reader'
import readerWasmUrl from 'zxing-wasm/reader/zxing_reader.wasm?url'
import { readLowQualityProductionQr } from './productionQrEnhance'

// WASM 随网页从内网加载，不使用组件默认的外网 CDN。
prepareZXingModule({ overrides: { locateFile: () => readerWasmUrl } })

async function decode(pixels: ImageData): Promise<string | undefined> {
  const results = await readBarcodes(pixels, { formats: ['QRCode'], maxNumberOfSymbols: 1,
    tryHarder: true, tryRotate: true, tryInvert: true, tryDownscale: true, textMode: 'Plain' })
  return results.find(result => result.isValid && result.text.trim())?.text.trim() || undefined
}

export async function readProductionQrFrame(source: CanvasImageSource, width: number, height: number, maxEdge = 1600): Promise<string | undefined> {
  if (!width || !height) return
  const scale = Math.min(1, maxEdge / Math.max(width, height))
  const canvas = document.createElement('canvas')
  canvas.width = Math.max(1, Math.round(width * scale)); canvas.height = Math.max(1, Math.round(height * scale))
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context) throw new Error('当前浏览器无法读取图像，请使用手机系统浏览器重试。')
  context.drawImage(source, 0, 0, canvas.width, canvas.height)
  const pixels = context.getImageData(0, 0, canvas.width, canvas.height)
  const full = await decode(pixels)
  if (full) return full
  // 中央区域保留更多原始像素，减少整张图纸缩小后小二维码丢失细节。
  const side = Math.min(width, height) * .75, cropEdge = Math.round(Math.min(side, maxEdge))
  canvas.width = cropEdge; canvas.height = cropEdge
  context.drawImage(source, (width - side) / 2, (height - side) / 2, side, side, 0, 0, cropEdge, cropEdge)
  const cropPixels = context.getImageData(0, 0, cropEdge, cropEdge)
  const cropped = await decode(cropPixels)
  if (cropped) return cropped
  const enhanced = await readLowQualityProductionQr(pixels) || await readLowQualityProductionQr(cropPixels)
  if (enhanced) return enhanced
  return jsQR(pixels.data, pixels.width, pixels.height, { inversionAttempts: 'attemptBoth' })?.data.trim() || undefined
}

export async function readProductionQrPhoto(file: File): Promise<string> {
  if (file.type && !file.type.startsWith('image/')) throw new Error('请选择图纸或批次流转卡的照片。')
  if (file.size > 20 * 1024 * 1024) throw new Error('照片超过20MB，请降低拍照分辨率后重试。')
  const url = URL.createObjectURL(file)
  try {
    const image = new Image()
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve()
      image.onerror = () => reject(new Error('无法读取照片，请使用JPG或PNG照片重新拍摄。'))
      image.src = url
    })
    const code = await readProductionQrFrame(image, image.naturalWidth, image.naturalHeight, 2400)
    if (!code) throw new Error('未识别到二维码，请靠近图纸或流转卡的二维码，保持清晰、完整并避免反光后重新拍摄。')
    return code
  } finally { URL.revokeObjectURL(url) }
}
