import { strFromU8, strToU8, unzipSync, zipSync } from 'three/examples/jsm/libs/fflate.module.js'
import { createProcurementWorkbook } from './procurementWorkbook'
import type { ProjectExportMetadata } from './exportFormat'

export const oplExportHeaders = ['序号', '子项目', '优先级', '实施状态', '问题描述', '附件', '解决措施', '需求日期', '负责人', '完成日期', '提交日期', '提交人']
const widths = [10, 18, 10, 12, 50, 22, 50, 14, 12, 14, 14, 14]
export function createOplWorkbook(rows: (string | number)[][], metadata: ProjectExportMetadata): Uint8Array {
  const files = unzipSync(createProcurementWorkbook(oplExportHeaders, rows, 'OPL开口问题清单', metadata))
  const parser = new DOMParser(), serializer = new XMLSerializer()
  const sheet = parser.parseFromString(strFromU8(files['xl/worksheets/sheet1.xml']!), 'application/xml')
  const styles = parser.parseFromString(strFromU8(files['xl/styles.xml']!), 'application/xml')
  const formats = styles.getElementsByTagName('cellXfs')[0]!
  const body = [...formats.children].find(format => format.getElementsByTagName('alignment')[0]?.getAttribute('horizontal') === 'left' && format.getAttribute('borderId') !== '0')!.cloneNode(true) as Element
  const alignment = body.getElementsByTagName('alignment')[0]!
  alignment.setAttribute('horizontal', 'general'); alignment.setAttribute('vertical', 'top')
  const bodyStyle = formats.children.length
  formats.appendChild(body); formats.setAttribute('count', String(formats.children.length))
  ;[...sheet.getElementsByTagName('col')].forEach((col, index) => col.setAttribute('width', String(widths[index])))
  ;[...sheet.getElementsByTagName('row')].filter(row => Number(row.getAttribute('r')) >= 8).forEach((row, index) => {
    const lines = Math.max(1, ...rows[index]!.map((value, column) => String(value).split('\n').reduce((total, line) => total + Math.max(1, Math.ceil([...line].reduce((length, char) => length + (char.charCodeAt(0) > 255 ? 2 : 1), 0) / (widths[column]! - 2))), 0)))
    row.setAttribute('ht', String(Math.min(409, Math.max(22, lines * 15 + 6)))); row.setAttribute('customHeight', '1')
    ;[...row.children].forEach(cell => cell.setAttribute('s', String(bodyStyle)))
  })
  files['xl/worksheets/sheet1.xml'] = strToU8(serializer.serializeToString(sheet))
  files['xl/styles.xml'] = strToU8(serializer.serializeToString(styles))
  return zipSync(Object.fromEntries(Object.entries(files).map(([name, content]) => [name, new Uint8Array(content)])))
}
export function downloadOplWorkbook(fileName: string, rows: (string | number)[][], metadata: ProjectExportMetadata) {
  const bytes = createOplWorkbook(rows, metadata)
  const url = URL.createObjectURL(new Blob([new Uint8Array(bytes)], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))
  const link = document.createElement('a'); link.href = url; link.download = fileName.replace(/[\\/:*?"<>|]/g, '_')
  document.body.append(link); link.click(); link.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
