import jsQR from 'jsqr'

export function readProductionQrFrame(source: CanvasImageSource, width: number, height: number, maxEdge = 1280): string | undefined {
  if (!width || !height) return
  const scale = Math.min(1, maxEdge / Math.max(width, height))
  const canvas = document.createElement('canvas')
  canvas.width = Math.max(1, Math.round(width * scale)); canvas.height = Math.max(1, Math.round(height * scale))
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context) throw new Error('当前浏览器无法读取图像，请使用手机系统浏览器重试。')
  context.drawImage(source, 0, 0, canvas.width, canvas.height)
  const pixels = context.getImageData(0, 0, canvas.width, canvas.height)
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
    const code = readProductionQrFrame(image, image.naturalWidth, image.naturalHeight, 1600)
      || readProductionQrFrame(image, image.naturalWidth, image.naturalHeight, 2400)
    if (!code) throw new Error('未识别到二维码，请靠近图纸或流转卡的二维码，保持清晰、完整并避免反光后重新拍摄。')
    return code
  } finally { URL.revokeObjectURL(url) }
}
