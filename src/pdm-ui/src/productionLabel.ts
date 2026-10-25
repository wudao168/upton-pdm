import { readProductionLotQr } from './api'
import type { ProductionReleaseRecord } from './productionTracking'

export async function openProductionLabel(record: ProductionReleaseRecord, lotId: string, token: string, width = 60, height = 40) {
  const popup = window.open('', '_blank')
  if (!popup) throw new Error('标签窗口未能打开，请允许本站弹出窗口后重新打印。')
  popup.document.title = '生产二维码标签'
  popup.document.body.textContent = '正在准备二维码标签…'
  try {
    const lot = record.state.lots.find(lot => lot.id === lotId)!, line = record.state.lines.find(line => line.id === lot.lineId)!
    const qr = await readProductionLotQr(record.state.releasePackageId, lotId, token)
    if (popup.closed) return
    const url = URL.createObjectURL(qr), doc = popup.document
    width = Math.max(40, Math.min(150, width)); height = Math.max(25, Math.min(150, height))
    doc.body.textContent = ''
    const style = doc.createElement('style')
    style.textContent = `@page{size:${width}mm ${height}mm;margin:0}body{margin:12px;font:12px "Microsoft YaHei",sans-serif;color:#111}button{padding:10px;margin-bottom:12px}article{box-sizing:border-box;width:${width}mm;height:${height}mm;padding:2mm;border:1px dashed #888;display:grid;grid-template-columns:24mm 1fr;gap:2mm;overflow:hidden}img{width:24mm;height:24mm;align-self:center}section{min-width:0;align-self:center;font-size:9px;overflow-wrap:anywhere}p{margin:2px 0}strong{font-size:11px}@media print{body{margin:0}button,aside{display:none}article{border:0}}`
    doc.head.append(style)
    const print = doc.createElement('button'); print.textContent = '选择打印机并打印标签'; print.onclick = () => popup.print()
    const hint = doc.createElement('aside'); hint.textContent = `标签尺寸 ${width} × ${height} 毫米。请选择已连接的标签打印机，使用实际大小打印，并关闭页眉页脚。`
    const label = doc.createElement('article'), image = doc.createElement('img'), text = doc.createElement('section')
    image.alt = '生产批次流转二维码'; image.src = url
    for (const [index, value] of [`${line.drawingNumber} · ${line.revision}`, line.name, `数量：${lot.quantity}`, `项目：${record.state.projectCode}`, `发布包：${record.state.packageNumber}`, `批次：${lot.id.slice(0, 8)}`].entries()) {
      const item = doc.createElement(index === 0 ? 'strong' : 'p'); item.textContent = value; text.append(item)
    }
    label.append(image, text); doc.body.append(print, hint, label)
    popup.onbeforeunload = () => URL.revokeObjectURL(url)
  } catch { if (!popup.closed) popup.close(); throw new Error('二维码标签准备失败，请检查网络和图纸访问权限后重新打印。') }
}
