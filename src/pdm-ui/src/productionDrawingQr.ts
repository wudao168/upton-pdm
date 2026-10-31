import { prepareZXingModule, writeBarcode } from 'zxing-wasm/writer'
import writerUrl from 'zxing-wasm/writer/zxing_writer.wasm?url'

prepareZXingModule({ overrides: { locateFile: () => writerUrl } })

export async function createProductionDrawingQr(content?: string | null) {
  if (!content?.trim()) throw new Error('该图纸缺少正式二维码数据，请刷新后重试。')
  return createQr(content, '图纸')
}

export async function createNameplateQr(serialNumber: string) {
  const content = serialNumber.trim()
  if (!content) throw new Error('铭牌缺少设备序列号，请核对发布内容。')
  return createQr(content, '铭牌')
}

async function createQr(content: string, kind: string) {
  const result = await writeBarcode(content, { format: 'QRCode', options: 'ecLevel=M', scale: 4, addQuietZones: true })
  if (result.error || !result.svg) throw new Error(`${kind}二维码生成失败，请重试。`)
  return { content, src: `data:image/svg+xml;charset=utf-8,${encodeURIComponent(result.svg)}` }
}
