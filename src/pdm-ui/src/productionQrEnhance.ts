import { readBarcodes, type ReadResult } from 'zxing-wasm/reader'

function candidatePixels(source: ImageData, result: ReadResult) {
  const points = Object.values(result.position)
  if (!points.every(point => Number.isFinite(point.x) && Number.isFinite(point.y))) return
  const xs = points.map(point => point.x), ys = points.map(point => point.y)
  const size = Math.min(Math.max(...xs) - Math.min(...xs), Math.max(...ys) - Math.min(...ys))
  if (size < 21) return
  const margin = Math.ceil(size / 4)
  const left = Math.max(0, Math.floor(Math.min(...xs)) - margin), top = Math.max(0, Math.floor(Math.min(...ys)) - margin)
  const right = Math.min(source.width, Math.ceil(Math.max(...xs)) + margin), bottom = Math.min(source.height, Math.ceil(Math.max(...ys)) + margin)
  if (right <= left || bottom <= top) return
  const scale = Math.min(1, 512 / Math.max(right - left, bottom - top))
  const pixels = new ImageData(Math.max(1, Math.round((right - left) * scale)), Math.max(1, Math.round((bottom - top) * scale)))
  for (let y = 0; y < pixels.height; y++) for (let x = 0; x < pixels.width; x++) {
    const from = ((top + Math.min(bottom - top - 1, Math.floor(y / scale))) * source.width
      + left + Math.min(right - left - 1, Math.floor(x / scale))) * 4
    pixels.data.set(source.data.subarray(from, from + 4), (y * pixels.width + x) * 4)
  }
  const version = Number.parseInt(result.version, 10)
  const modules = Number.isFinite(version) && version >= 1 && version <= 40 ? version * 4 + 17 : 25
  return { pixels, radius: Math.max(3, Math.min(31, Math.floor(size * scale / modules * 1.75))) }
}

function removeTexture(pixels: ImageData, radius: number, redChannel: boolean) {
  const { width, height, data } = pixels, gray = new Uint8Array(width * height), smooth = new Uint8Array(gray.length)
  let darkest = 255, lightest = 0
  for (let i = 0; i < gray.length; i++) {
    const value = redChannel ? data[i * 4]! : data[i * 4]! * .299 + data[i * 4 + 1]! * .587 + data[i * 4 + 2]! * .114
    gray[i] = value; darkest = Math.min(darkest, value); lightest = Math.max(lightest, value)
  }
  if (lightest - darkest < 6) return
  const neighbors = new Array<number>(9)
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    let index = 0
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++)
      neighbors[index++] = gray[Math.max(0, Math.min(height - 1, y + dy)) * width + Math.max(0, Math.min(width - 1, x + dx))]!
    for (let i = 1; i < 9; i++) {
      const value = neighbors[i]!; let j = i - 1
      while (j >= 0 && neighbors[j]! > value) { neighbors[j + 1] = neighbors[j]!; j-- }
      neighbors[j + 1] = value
    }
    smooth[y * width + x] = neighbors[4]!
  }
  // 积分图计算邻域均值；处理中保留局部明暗差异，避免整张照片使用一个阈值。
  const stride = width + 1, integral = new Uint32Array(stride * (height + 1))
  for (let y = 0; y < height; y++) {
    let sum = 0
    for (let x = 0; x < width; x++) { sum += smooth[y * width + x]!; integral[(y + 1) * stride + x + 1] = integral[y * stride + x + 1]! + sum }
  }
  const binary = new ImageData(width, height)
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const left = Math.max(0, x - radius), top = Math.max(0, y - radius)
    const right = Math.min(width, x + radius + 1), bottom = Math.min(height, y + radius + 1)
    const sum = integral[bottom * stride + right]! - integral[top * stride + right]!
      - integral[bottom * stride + left]! + integral[top * stride + left]!
    const value = smooth[y * width + x]! < sum / ((right - left) * (bottom - top)) - 6 ? 0 : 255
    const index = (y * width + x) * 4
    binary.data[index] = binary.data[index + 1] = binary.data[index + 2] = value; binary.data[index + 3] = 255
  }
  return binary
}

/** 仅在常规识别失败后，对检测出的二维码区域进行有限次数增强；不接受校验失败的内容。 */
export async function readLowQualityProductionQr(pixels: ImageData): Promise<string | undefined> {
  const options = { formats: ['QRCode'] as ['QRCode'], maxNumberOfSymbols: 1, tryHarder: true, textMode: 'Plain' as const }
  async function enhancedCode(image: ImageData, radius: number) {
    // 红通道减少屏幕子像素的彩色纹理影响；灰度通道兼容其他拍摄场景。
    for (const redChannel of [true, false]) {
      const enhanced = removeTexture(image, radius, redChannel)
      if (!enhanced) continue
      const decoded = await readBarcodes(enhanced, { ...options, binarizer: 'BoolCast' })
      const valid = decoded.find(item => item.isValid && item.text.trim())
      if (valid) return valid.text.trim()
    }
  }
  for (const binarizer of ['GlobalHistogram', 'FixedThreshold'] as const) {
    const results = await readBarcodes(pixels, { ...options, binarizer, returnErrors: true })
    for (const result of results) {
      if (result.isValid && result.text.trim()) return result.text.trim()
      if (result.format !== 'QRCode') continue
      const candidate = candidatePixels(pixels, result)
      if (!candidate) continue
      const code = await enhancedCode(candidate.pixels, candidate.radius)
      if (code) return code
    }
  }
  // 已经裁成小图的照片，边缘干扰可能导致定位失败；此时直接增强小图。
  if (Math.max(pixels.width, pixels.height) <= 768) return enhancedCode(pixels, 7)
}
