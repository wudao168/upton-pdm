import { describe, expect, it } from 'vitest'
import { bomRemovalHistory } from '../src/bomRemovalHistory'
import type { BomItem, BomVersion, ReleasePackageSummary } from '../src/types'

const part: BomItem = { id: 'part', sequence: 1, kind: 'Standard', drawingNumber: '01020016699', name: 'T型支座', specification: 'SH12', brand: '美亚特', quantity: 4, unit: '001', revision: 'W1', complete: true }
const release = (number: number, items: BomItem[], removed: string[] = [], changes: Partial<ReleasePackageSummary> = {}): ReleasePackageSummary => ({
  id: `release-${number}`, number: `RP-${number}`, state: '已发布', scope: number === 1 ? 'StandardFormal' : 'StandardSupplement',
  standardBomSnapshot: items, nonStandardBomSnapshot: [], electricalBomSnapshot: [], selectedBomItemIds: [], confirmedRemovedBomItemIds: removed,
  steps: [], workflowVersion: 1, createsManufacturingBaseline: false, locksDocuments: false, wholeSetMultiplier: 1,
  createdAt: `2026-11-0${number}T00:00:00Z`, publishedAt: `2026-11-0${number}T01:00:00Z`, ...changes,
})

describe('已发布移除历史', () => {
  it('保留移除前冻结信息与当时倍率，后续发布不重复记载', () => {
    const packages = [release(1, [part], [], { wholeSetMultiplier: 3, standardBomRevision: 'S-TEST-V01' }), release(2, [], ['PART']), release(3, [])]
    const frozen = JSON.stringify(packages)
    const history = bomRemovalHistory('Standard', [], packages.slice().reverse())
    expect(history).toHaveLength(1)
    expect(history[0]).toMatchObject({ packageNumber: 'RP-2', releasedAt: '2026-11-02T01:00:00Z', baselineLabel: 'S-TEST-V01', item: { name: 'T型支座', quantity: 12, specification: 'SH12' } })
    expect(JSON.stringify(packages)).toBe(frozen)
  })

  it('补充发布未勾选、草稿、长交期及其他BOM分类不误报移除', () => {
    const packages = [release(1, [part]), release(2, []), release(3, [], ['part'], { state: '审批中' }),
      release(4, [], ['part'], { scope: 'StandardLongLead' }), release(5, [], ['part'], { scope: 'ElectricalSupplement' })]
    expect(bomRemovalHistory('Standard', [], packages)).toEqual([])
  })

  it('旧记录缺少发布时间时不将创建时间冒充移除时间', () => {
    expect(bomRemovalHistory('Standard', [], [release(1, [part]), release(2, [], ['part'], { publishedAt: undefined })])[0]?.releasedAt).toBe('')
  })

  it('相同料号的配置分别保留，恢复后再次移除保留两次发布', () => {
    const a = { ...part, id: 'a', engineeringKitReferenceId: 'kit-a', quantity: 2 }
    const b = { ...part, id: 'b', engineeringKitReferenceId: 'kit-b', quantity: 3 }
    const packages = [release(1, [a, b]), release(2, [b], ['a']), release(3, [a, b]), release(4, [], ['a', 'b'])]
    const history = bomRemovalHistory('Standard', [], packages)
    expect(history).toHaveLength(3)
    expect(history.filter(entry => entry.item.engineeringKitReferenceId === 'kit-a').map(entry => entry.packageNumber)).toEqual(['RP-4', 'RP-2'])
    expect(history.filter(entry => entry.packageNumber === 'RP-4').map(entry => entry.item.quantity).sort()).toEqual([2, 3])
  })

  it('已发布版本与发布包只计一次，多个原始实例合计移除前总量', () => {
    const rows = [{ ...part, id: 'instance-a', quantity: 2 }, { ...part, id: 'instance-b', quantity: 2 }]
    const version = (number: number, items: BomItem[]): BomVersion => ({ id: `version-${number}`, projectId: 'project', kind: 'Standard', versionNumber: number, label: `S-TEST-V0${number}`, state: 'Released', items,
      createdAt: `2026-11-0${number}T00:00:00Z`, releasedAt: `2026-11-0${number}T01:00:00Z`, updatedAt: '', createdBy: 'admin', updatedBy: 'admin' })
    const packages = [release(1, rows, [], { standardBomVersionId: 'version-1' }), release(2, [], ['instance-a', 'instance-b'], { standardBomVersionId: 'version-2' })]
    expect(bomRemovalHistory('Standard', [version(1, rows), version(2, [])], packages)).toMatchObject([{ item: { quantity: 4 }, packageNumber: 'RP-2', baselineLabel: 'S-TEST-V01' }])
    expect(bomRemovalHistory('Standard', [version(1, rows), version(2, [])], [])).toMatchObject([{ item: { quantity: 4 }, packageNumber: undefined }])
  })
})
