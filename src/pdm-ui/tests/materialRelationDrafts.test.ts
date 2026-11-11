import { describe, expect, it } from 'vitest'
import { recoverRelationAllocations } from '../src/materialRelationDrafts'
import type { MaterialRelationAllocation, MaterialRelationMainCheck } from '../src/types'

const option = { id: 'new-option', materialId: 'part', materialCode: 'PART', materialName: '配件', materialKind: 'Standard' as const,
  unitCode: '001', quantityMode: 'PerMainQuantity' as const, quantityPerSet: 2, isDefault: false, sortOrder: 1 }
const main: MaterialRelationMainCheck = { mainBomItemId: 'main', mainMaterialCode: 'MAIN', mainMaterialName: '主料',
  mainQuantity: 3, templateId: 'template', revisionId: 'v2', revisionVersion: 2, isComplete: false,
  groups: [{ groupId: 'new-group', groupName: '驱动器', isRequired: true, selectionMode: 'Single', maxSelection: 1,
    isComplete: false, status: '待核对', expectedQuantity: 0, actualQuantity: 0, selectedOptionIds: [], options: [option] }] }
const saved: MaterialRelationAllocation[] = [{ id: 'a', name: '组合A', quantity: 1, revisionId: 'v1', revisionVersion: 1,
  groups: [{ groupId: 'old-group', groupName: '驱动器', optionIds: ['old-option'], confirmNoAccessory: false,
    options: [{ optionId: 'old-option', materialCode: 'PART', materialName: '配件', unitCode: '001',
      quantityMode: 0, quantityPerSet: 2, quantity: 2 }] }] },
{ id: 'b', name: '组合B', quantity: 2, revisionId: 'v1', revisionVersion: 1,
  groups: [{ groupId: 'old-group', groupName: '驱动器', optionIds: [], confirmNoAccessory: true }] }]

describe('历史选配恢复为新版待保存草稿', () => {
  it('内部编号变化时按组名、料号和数量规则恢复选择，保留各组合数量并冻结原记录', () => {
    const before = JSON.stringify(saved)
    const recovered = recoverRelationAllocations(main, saved)!
    expect(recovered.map(item => [item.id, item.quantity, item.revisionId])).toEqual([['a', 1, 'v2'], ['b', 2, 'v2']])
    expect(recovered[0]!.groups[0]!.optionIds).toEqual(['new-option'])
    expect(recovered[1]!.groups[0]!.confirmNoAccessory).toBe(false)
    expect(JSON.stringify(saved)).toBe(before)
  })
  it.each([{ quantityPerSet: 3 }, { quantityMode: 'Fixed' as const }, { unitCode: '002' }, { materialCode: 'OTHER' }])(
    '不恢复已变化的规则 %j', change => {
      expect(recoverRelationAllocations({ ...main, groups: [{ ...main.groups[0]!, options: [{ ...option, ...change }] }] }, saved)![0]!.groups[0]!.optionIds).toEqual([])
    })
  it('不猜测重名组、重名候选或已移除配件', () => {
    for (const groups of [[...main.groups, ...main.groups], [{ ...main.groups[0]!, options: [option, { ...option, id: 'duplicate' }] }], [{ ...main.groups[0]!, options: [] }]])
      expect(recoverRelationAllocations({ ...main, groups }, saved)![0]!.groups[0]!.optionIds).toEqual([])
  })
  it('当前配置已有记录、数量不一致或无历史记录时不覆盖', () => {
    expect(recoverRelationAllocations({ ...main, isComplete: true }, saved)).toBeNull()
    expect(recoverRelationAllocations({ ...main, allocations: saved }, saved)).toBeNull()
    expect(recoverRelationAllocations({ ...main, mainQuantity: 4 }, saved)).toBeNull()
    expect(recoverRelationAllocations(main, [])).toBeNull()
    expect(recoverRelationAllocations(main, saved.map(item => ({ ...item, revisionId: 'v2' })))).toBeNull()
  })
})
