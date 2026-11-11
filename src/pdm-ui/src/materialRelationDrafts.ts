import type { MaterialRelationAllocation, MaterialRelationMainCheck } from './types'

const normalized = (value: string) => value.trim().toUpperCase()
const quantityMode = (value: string | number) => value === 0 || value === 'PerMainQuantity' ? 'per-main' : 'fixed'

// 只恢复为待保存草稿；新版配置仍由保存接口重新校验，历史快照保持原值。
export function recoverRelationAllocations(main: MaterialRelationMainCheck, saved: MaterialRelationAllocation[]) {
  if (main.isComplete || main.allocations?.length || !saved.length || saved.some(item => item.revisionId === main.revisionId)
    || saved.some(item => !Number.isFinite(item.quantity) || item.quantity <= 0)
    || Math.abs(saved.reduce((sum, item) => sum + item.quantity, 0) - main.mainQuantity) > 0.00001) return null
  return saved.map(allocation => ({ ...allocation, revisionId: main.revisionId, revisionVersion: main.revisionVersion,
    groups: main.groups.map(group => {
      const matches = allocation.groups.filter(old => old.groupName.trim() === group.groupName.trim())
      const old = matches.length === 1 && main.groups.filter(item => item.groupName.trim() === group.groupName.trim()).length === 1
        ? matches[0] : undefined
      const selected = old?.optionIds.map(id => {
        const frozen = old.options?.find(option => option.optionId === id)
        if (!frozen) return undefined
        const candidates = group.options.filter(option => normalized(option.materialCode) === normalized(frozen.materialCode)
          && option.unitCode === frozen.unitCode && quantityMode(option.quantityMode) === quantityMode(frozen.quantityMode)
          && option.quantityPerSet === frozen.quantityPerSet)
        return candidates.length === 1 ? candidates[0]!.id : undefined
      }) ?? []
      const optionIds = selected.every(id => !!id) && (group.maxSelection == null || selected.length <= group.maxSelection)
        ? selected as string[] : []
      return { groupId: group.groupId, groupName: group.groupName, optionIds,
        // 新版组的“无需配套”必须重新确认，不能从旧版静默继承。
        confirmNoAccessory: false, noAccessoryReason: null }
    }) }))
}
