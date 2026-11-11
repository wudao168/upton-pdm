import type { ExpenseStep } from './expenseRequests'
export interface BonusDepartment { key: string; name: string; unitId: string | null; reviewers: string[] | null }
export interface BonusIndicator { id: string; name: string; maxScore: number; weight: number; guidance: string; evidence: string; departments: { departmentKey: string; weight: number }[] }
export interface BonusRole { id: string; name: string; phase: string; suggestedWeight: number; indicators: BonusIndicator[] }
export interface BonusSettings { version: number; enabled: boolean; departments: BonusDepartment[]; roles: BonusRole[]; projectIndicators: BonusIndicator[]; approvalNodes: { id: string; name: string; departmentKeys: string[]; parallel: boolean }[]; paymentDepartmentKey: string }
export interface BonusUser { username: string; name: string }
export interface BonusSettingsView { settings: BonusSettings; canEdit: boolean; users: BonusUser[]; departments: { id: string; name: string }[] }
export interface BonusPerson { id: string; username: string; roleId: string; weight: number; reason: string }
export interface BonusEvidence { indicatorId: string; personId: string; text: string }
export interface BonusScore { indicatorId: string; personId: string; departmentKey: string; score: number | null; comment: string; actor?: string | null; at?: string | null }
export interface BonusTask { indicatorId: string; personId: string; departmentKey: string; members: string[] }
export interface BonusAward { personId: string; username: string; share: number; roleScore: number; amount: number }
export interface BonusApplication { phase: string; status: string; applicant: string; people: BonusPerson[]; evidence: BonusEvidence[]; milestoneEvidence: string; template: BonusSettings | null; scoringTasks: BonusTask[]; scores: BonusScore[]; steps: ExpenseStep[]; awards: BonusAward[]; projectScore: number | null; audit: { actor: string; action: string; comment: string; at: string }[]; payment: { amount: number; date: string; voucher: string; actor: string; at: string } | null }
export interface BonusApplicationView { application: BonusApplication; canEdit: boolean; canSubmit: boolean; canApprove: boolean; canPay: boolean; myScoringTasks: BonusTask[] }
export interface BonusView { projectId: string; projectCode: string; projectName: string; rowVersion: number; pool: number | null; frozen: boolean; canCreate: boolean; actor: string; applications: BonusApplicationView[]; settings: BonusSettings | null; users: BonusUser[] }
export interface BonusBook { projectId: string; rowVersion: number; frozenPool: number | null; applications: BonusApplication[] }
export const bonusPhase = (phase: string) => phase === 'Shipment' ? '发货' : '终验收'
export const bonusStatus = (status?: string) => ({ Draft: '草稿', Scoring: '部门评分中', Scored: '待经理确认', Pending: '审批中', Approved: '已批准', Paid: '已登记发放', Rejected: '已退回', Withdrawn: '已撤回' }[status ?? ''] ?? '尚未申请')
export const bonusMoney = (value: number | null | undefined) => value == null ? '—' : `¥ ${value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
// Works in the supported LAN HTTP environment as well as secure contexts; identifiers are not credentials.
export const bonusId = () => globalThis.crypto?.randomUUID?.() ?? `bonus-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
