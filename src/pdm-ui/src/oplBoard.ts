import type { OplRow } from './projectOpl'
export const oplStates = ['待处理', '处理中', '待确认', '已完成', '已作废'] as const
/** 看板只放行服务端已有权限，确认完成继续走专用接口。 */
export function canMoveOpl(row: OplRow, target: string): boolean {
  if (!(oplStates as readonly string[]).includes(target) || target === row.issue.status) return false
  if (target === '已完成') return row.canComplete && !['已完成', '已作废'].includes(row.issue.status)
  return row.canEdit
}
