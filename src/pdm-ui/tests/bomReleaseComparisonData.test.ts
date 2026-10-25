import { describe, expect, it } from 'vitest'
import { compareReleaseSnapshots, compareSnapshotTime, releaseComparisonSnapshots } from '../src/bomReleaseComparison'
import type { BomComparisonSnapshot } from '../src/bomReleaseComparison'
import type { BomItem, BomVersion, ReleasePackageSummary } from '../src/types'

const item = (code: string, quantity = 1, extra: Partial<BomItem> = {}): BomItem => ({ sequence: 1, drawingNumber: code, name: code.trim().toUpperCase(), quantity, unit: '001', revision: 'A', complete: true, ...extra })
const snapshot = (id: string, items: BomItem[], versionNumber = 1): BomComparisonSnapshot => ({ id, label: id, items, versionNumber, releasedAt: `2026-10-${10 + versionNumber}T00:00:00Z` })

describe('发布包冻结BOM比较', () => {
  it('每个已发布包使用独立快照，过滤草稿及其他分类，保留无关联包的历史正式版本', () => {
    const versions = [
      { id: 'v1', kind: 'Standard', state: 'Released', label: 'S-B01', versionNumber: 1, items: [item('错误回退值')], createdAt: '2026-10-10' },
      { id: 'v2', kind: 'Standard', state: 'Released', label: 'S-V02', versionNumber: 2, items: [item('历史版本')], createdAt: '2026-10-12' },
      { id: 'draft', kind: 'Standard', state: 'Draft', items: [item('草稿')] },
    ] as BomVersion[]
    const packages = [
      { id: 'p1', number: 'RP-1', state: '已发布', scope: 'StandardFormal', standardBomVersionId: 'v1', publishedAt: '2026-10-11', standardBomSnapshot: [item('冻结1')] },
      { id: 'p2', number: 'RP-2', state: '已发布', scope: 'StandardSupplement', standardBomVersionId: 'v1', publishedAt: '2026-10-13', standardBomSnapshot: [item('冻结2')] },
      { id: 'draft', number: '未发布', state: '审批中', scope: 'StandardFormal' },
      { id: 'electrical', state: '已发布', scope: 'ElectricalFormal' },
    ] as ReleasePackageSummary[]
    const result = releaseComparisonSnapshots('Standard', versions, packages)
    expect(result.map(row => row.id)).toEqual(['package:p1', 'version:v2', 'package:p2'])
    expect(result.map(row => row.items[0]?.drawingNumber)).toEqual(['冻结1', '历史版本', '冻结2'])
    expect(result[0]?.packageNumber).toBe('RP-1')
    expect(result[0]?.label).toBe('S-V01')
  })

  it('同料号汇总且不修改源数据，各容器使用相同行键', () => {
    const a = [item('A', 0.1), item(' a ', 0.2), item('B', 1, { manuallyExcluded: true })]
    const before = JSON.stringify(a)
    const rows = compareReleaseSnapshots([snapshot('v1', a), snapshot('v2', [item('A', 0.3), item('C')], 2)])
    expect(rows.map(row => row.code)).toEqual(['A', 'C'])
    expect(rows[0]?.cells[0]?.item?.quantity).toBe(0.3)
    expect(rows[0]?.cells[1]?.status).toBe('same')
    expect(rows[1]?.cells.map(cell => cell.status)).toEqual(['missing', 'added'])
    expect(JSON.stringify(a)).toBe(before)
  })

  it('以最早选择项标记增删改，空快照保留占位', () => {
    const snapshots = [snapshot('old', [item('A', 2), item('B'), item('C'), item('D')]),
      snapshot('mid', [item('A', 3), item('B', 1, { revision: 'B' }), item('D', 1, { brand: 'UPTON', isWearPart: true }), item('E')], 2), snapshot('new', [], 3)]
    const rows = compareReleaseSnapshots(snapshots)
    expect(rows.map(row => row.cells.length)).toEqual([3, 3, 3, 3, 3])
    expect(rows.find(row => row.code === 'A')?.cells[1]).toMatchObject({ status: 'changed', label: '数量 2 → 3' })
    expect(rows.find(row => row.code === 'B')?.cells[1]?.changes).toEqual(['版次 A → B'])
    expect(rows.find(row => row.code === 'C')?.cells[1]?.status).toBe('removed')
    expect(rows.find(row => row.code === 'D')?.cells[1]?.changes).toContain('易损件 否 → 是')
    expect(rows.find(row => row.code === 'E')?.cells.map(cell => cell.status)).toEqual(['missing', 'added', 'missing'])
    expect(rows.filter(row => row.cells[2]?.status === 'removed')).toHaveLength(4)
  })

  it('按发布时间升序排列', () => {
    expect([snapshot('v3', [], 3), snapshot('v1', [], 1), snapshot('v2', [], 2)].sort(compareSnapshotTime).map(row => row.id)).toEqual(['v1', 'v2', 'v3'])
  })
})
