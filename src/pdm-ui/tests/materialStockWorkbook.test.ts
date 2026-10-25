import { afterEach, expect, it } from 'vitest'
import { unzipSync, strFromU8 } from 'three/examples/jsm/libs/fflate.module.js'
import { refreshMaterialStockPlans, setMaterialStockContext } from '../src/materialStockPlans'
import { createProcurementWorkbook } from '../src/procurementWorkbook'
import { markStockedMaterials } from '../src/materialStockWorkbook'
afterEach(() => setMaterialStockContext(''))
it('exports stock labels and purple literal codes without changing quantities or duplicating classification', async () => {
  setMaterialStockContext('export')
  await refreshMaterialStockPlans(async () => ({ enterpriseCode: '01', organizationCode: '7', items: [{ enterpriseCode: '01', organizationCode: '7', materialCode: '01020014079', safetyStockQuantity: 15, reorderQuantity: 50, isEffective: true, unitName: '个', syncedAt: '2026-10-23T08:00:00Z' }] }))
  const metadata = { title: '备料', company: 'UPTON', scope: '子项目', versionLabel: '版本', version: 'V1', exportedAt: '2026-10-23', exportedBy: '工程师', fields: [] }
  const bytes = createProcurementWorkbook(['料号', '名称', '数量'], [['01020014079', '键盘', 3], ['UKIT-000001', '套件', 2]], '备料', metadata)
  const files = unzipSync(bytes), sheet = new DOMParser().parseFromString(strFromU8(files['xl/worksheets/sheet1.xml']!), 'application/xml')
  expect(sheet.querySelector('parsererror')).toBeNull()
  expect(sheet.querySelector('c[r="A8"]')?.textContent).toBe('01020014079')
  expect(sheet.querySelector('c[r="C8"]')?.textContent).toBe('3')
  expect(sheet.querySelector('c[r="D8"]')?.textContent).toBe('常备件')
  expect(sheet.querySelector('c[r="D9"]')).toBeNull()
  expect(sheet.querySelector('autoFilter')?.getAttribute('ref')).toBe('A7:D9')
  expect(strFromU8(files['xl/styles.xml']!)).toContain('FF7C3AED')
  expect(markStockedMaterials(bytes)).toBe(bytes)
})
