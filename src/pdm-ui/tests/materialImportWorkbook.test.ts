import { describe, expect, it } from 'vitest'
import { strFromU8, unzipSync } from 'three/examples/jsm/libs/fflate.module.js'
import { createMaterialImportTemplate, materialImportHeaders } from '../src/materialImportWorkbook'
import type { MaterialCategory } from '../src/types'

const category: MaterialCategory = {
  code: '0102', name: '机械外购件', parentCode: '01', pdmKind: 'Standard',
  defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true,
  numberPrefix: '0102', sequenceLength: 7, counterScope: '0102', sortOrder: 1,
  updatedBy: 'admin', updatedAt: '2026-09-14T00:00:00Z', rowVersion: 1, currentSequence: 1000000,
}

describe('料品批量导入 Excel 模板', () => {
  it('生成可解析的OOXML，含必填列、分类与单位下拉，并默认单位为个', () => {
    const bytes = createMaterialImportTemplate([category])
    expect([...bytes.slice(0, 2)]).toEqual([0x50, 0x4b])
    const files = unzipSync(bytes)
    for (const content of Object.values(files)) {
      const doc = new DOMParser().parseFromString(strFromU8(content), 'text/xml')
      expect(doc.querySelector('parsererror')).toBeNull()
    }

    const sheet = new DOMParser().parseFromString(strFromU8(files['xl/worksheets/sheet1.xml']), 'text/xml')
    expect([...sheet.querySelectorAll('row[r="1"] c')].map(cell => cell.textContent)).toEqual(materialImportHeaders)
    expect(sheet.querySelector('c[r="C2"]')?.textContent).toBe('个')
    expect(sheet.querySelector('pane')?.getAttribute('state')).toBe('frozen')
    expect(sheet.querySelector('dataValidation[sqref="A2:A1001"] formula1')?.textContent).toBe("'字典'!$A$2:$A$2")
    expect(sheet.querySelector('dataValidation[sqref="C2:C1001"] formula1')?.textContent).toBe("'字典'!$C$2:$C$13")

    const dictionary = new DOMParser().parseFromString(strFromU8(files['xl/worksheets/sheet2.xml']), 'text/xml')
    expect(dictionary.querySelector('c[r="A2"]')?.textContent).toBe('0102 机械外购件')
    const workbook = new DOMParser().parseFromString(strFromU8(files['xl/workbook.xml']), 'text/xml')
    expect(workbook.querySelector('sheet[name="字典"]')?.getAttribute('state')).toBe('hidden')
  })
})
