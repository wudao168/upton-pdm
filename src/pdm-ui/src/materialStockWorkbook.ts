import { strToU8, strFromU8, unzipSync, zipSync } from 'three/examples/jsm/libs/fflate.module.js'
import { materialStockPlan } from './materialStockPlans'

// 只处理有真实料号列的工作表，不改导入模板、项目计划或冻结业务数据。
export function markStockedMaterials(bytes: Uint8Array): Uint8Array {
  const files = unzipSync(bytes), parser = new DOMParser(), serializer = new XMLSerializer()
  const ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
  if (!files['xl/styles.xml']) return bytes
  const styles = parser.parseFromString(strFromU8(files['xl/styles.xml']!), 'application/xml')
  const fonts = styles.getElementsByTagName('fonts')[0]!, xfs = styles.getElementsByTagName('cellXfs')[0]!
  const purple = new Map<string, string>()
  const addStyle = (original: string) => {
    if (purple.has(original)) return purple.get(original)!
    const style = xfs.children[Number(original)]!.cloneNode(true) as Element
    const font = fonts.children[Number(style.getAttribute('fontId') || 0)]!.cloneNode(true) as Element
    font.querySelectorAll('color').forEach(element => element.remove())
    const color = styles.createElementNS(ns, 'color'); color.setAttribute('rgb', 'FF7C3AED'); font.appendChild(color)
    style.setAttribute('fontId', String(fonts.children.length)); style.setAttribute('applyFont', '1')
    fonts.appendChild(font); fonts.setAttribute('count', String(fonts.children.length))
    const index = String(xfs.children.length); xfs.appendChild(style); xfs.setAttribute('count', String(xfs.children.length)); purple.set(original, index)
    return index
  }
  const shared = files['xl/sharedStrings.xml'] ? [...parser.parseFromString(strFromU8(files['xl/sharedStrings.xml']!), 'application/xml').getElementsByTagName('si')].map(item => item.textContent || '') : []
  const text = (cell: Element) => cell.getAttribute('t') === 's' ? shared[Number(cell.querySelector('v')?.textContent)] || '' : cell.textContent || ''
  const column = (cell: Element) => cell.getAttribute('r')?.match(/^[A-Z]+/)?.[0] || ''
  const columnIndex = (value: string) => [...value].reduce((result, letter) => result * 26 + letter.charCodeAt(0) - 64, 0)
  const columnName = (value: number) => { let result = ''; for (; value > 0; value = Math.floor((value - 1) / 26)) result = String.fromCharCode(65 + (value - 1) % 26) + result; return result }
  let changed = false
  for (const path of Object.keys(files).filter(path => /^xl\/worksheets\/sheet\d+\.xml$/.test(path))) {
    const sheet = parser.parseFromString(strFromU8(files[path]!), 'application/xml'), rows = [...sheet.getElementsByTagName('row')]
    const header = rows.find(row => [...row.children].some(cell => ['物料编码', '料号', '图号'].includes(text(cell))))
    if (!header || [...header.children].some(cell => text(cell) === '常备件')) continue
    const code = column([...header.children].find(cell => ['物料编码', '料号', '图号'].includes(text(cell)))!)
    const name = [...header.children].find(cell => ['物料名称', '名称', '品名'].includes(text(cell)))
    const matched = rows.slice(rows.indexOf(header) + 1).filter(row => [...row.children].some(cell => column(cell) === code && materialStockPlan(text(cell).trim())))
    if (!matched.length) continue
    const next = columnName(Math.max(...rows.flatMap(row => [...row.children].map(cell => columnIndex(column(cell))))) + 1)
    const append = (row: Element, value: string, style: string) => {
      const cell = sheet.createElementNS(ns, 'c'); cell.setAttribute('r', next + row.getAttribute('r')); cell.setAttribute('s', style); cell.setAttribute('t', 'inlineStr')
      const inline = sheet.createElementNS(ns, 'is'), content = sheet.createElementNS(ns, 't'); content.textContent = value; inline.appendChild(content); cell.appendChild(inline); row.appendChild(cell)
    }
    append(header, '常备件', header.children[0]?.getAttribute('s') || '0')
    for (const row of matched) {
      for (const cell of [...row.children].filter(cell => column(cell) === code || name && column(cell) === column(name))) cell.setAttribute('s', addStyle(cell.getAttribute('s') || '0'))
      append(row, '常备件', addStyle('0'))
    }
    for (const range of sheet.querySelectorAll('autoFilter,dimension')) {
      const reference = range.getAttribute('ref')
      if (reference?.includes(':')) range.setAttribute('ref', reference.split(':')[0] + ':' + next + reference.split(':')[1]!.replace(/[A-Z]/g, ''))
    }
    files[path] = strToU8(serializer.serializeToString(sheet)); changed = true
  }
  if (!changed) return bytes
  files['xl/styles.xml'] = strToU8(serializer.serializeToString(styles))
  return zipSync(Object.fromEntries(Object.entries(files).map(([name, content]) => [name, new Uint8Array(content)])))
}
