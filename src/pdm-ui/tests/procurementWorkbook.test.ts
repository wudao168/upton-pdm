import { describe, expect, it } from 'vitest'
import { strFromU8, unzipSync } from 'three/examples/jsm/libs/fflate.module.js'
import { createProcurementWorkbook } from '../src/procurementWorkbook'

describe('采购跟踪 Excel 工作簿', () => {
  it('生成真实 OOXML，料号及公式形态文本保持文本，数量保持数字', () => {
    const bytes = createProcurementWorkbook(['料号', '名称', '数量'], [['03021000000', '=1+2<&"\u0001', 12.5], ['0001', '—', 0]])
    expect([...bytes.slice(0, 2)]).toEqual([0x50, 0x4b])
    const files = unzipSync(bytes)
    expect(Object.keys(files)).toHaveLength(5)
    for (const content of Object.values(files)) {
      const doc = new DOMParser().parseFromString(strFromU8(content), 'text/xml')
      expect(doc.querySelector('parsererror')).toBeNull()
    }
    const doc = new DOMParser().parseFromString(strFromU8(files['xl/worksheets/sheet1.xml']), 'text/xml')
    const rows = doc.querySelectorAll('row')
    expect(rows).toHaveLength(3)
    expect(rows[1].children[0].getAttribute('t')).toBe('inlineStr')
    expect(rows[1].children[0].textContent).toBe('03021000000')
    expect(rows[1].children[1].textContent).toBe('=1+2<&"')
    expect(rows[1].children[2].getAttribute('t')).toBeNull()
    expect(rows[1].children[2].textContent).toBe('12.5')
    expect(doc.querySelector('f')).toBeNull()
    expect(doc.querySelector('pane')?.getAttribute('state')).toBe('frozen')
  })
})
