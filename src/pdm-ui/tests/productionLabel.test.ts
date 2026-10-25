import { afterEach, expect, it, vi } from 'vitest'
import { openProductionLabel } from '../src/productionLabel'
import * as api from '../src/api'
import type { ProductionReleaseRecord } from '../src/productionTracking'
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })
it('按设置尺寸打印批次码，业务文字不作为HTML执行，打印不登记生产数量', async () => {
  const doc = document.implementation.createHTMLDocument(), print = vi.fn(), close = vi.fn()
  vi.spyOn(window, 'open').mockReturnValue({ document: doc, closed: false, print, close } as unknown as Window)
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn().mockReturnValue('blob:label'), revokeObjectURL: vi.fn() }))
  const qr = vi.spyOn(api, 'readProductionLotQr').mockResolvedValue(new Blob(['svg'], { type: 'image/svg+xml' }))
  const record: ProductionReleaseRecord = { rowVersion: 1, state: { releasePackageId: 'release', projectId: 'project', companyId: 'company', projectCode: 'P1', projectName: '项目', packageNumber: 'RP1', scope: '', publishedAt: '', lines: [{ id: 'line', materialKey: 'M', drawingNumber: 'MODEL', name: '<script>bad()</script>', revision: 'A', requiredQuantity: 4, unit: '件', priority: '' }], lots: [{ id: 'lot', lineId: 'line', route: 'Self', quantity: 4, paused: false, frozen: false, schedule: [] }], movements: [] } }
  await openProductionLabel(record, 'lot', 'token', 70, 45)
  expect(qr).toHaveBeenCalledWith('release', 'lot', 'token')
  expect(doc.querySelector('style')?.textContent).toContain('@page{size:70mm 45mm')
  expect(doc.body.textContent).toContain('<script>bad()</script>'); expect(doc.querySelector('script')).toBeNull()
  expect(doc.querySelector('img')?.src).toBe('blob:label'); doc.querySelector('button')!.click(); expect(print).toHaveBeenCalledOnce()
  expect(record.state.movements).toEqual([])
})
