export type ProjectFileFormKind = 'sample-demand' | 'customer-change' | 'pre-acceptance' | 'final-acceptance'

export interface ProjectAcceptanceDetails {
  contractNumber?: string | null
  location?: string | null
  attendees?: string | null
  standard?: string | null
  explanation?: string | null
  corrections?: string | null
  passed?: boolean | null
  comments?: string | null
  terms?: string | null
}

export function acceptanceDefaults(kind: ProjectFileFormKind): ProjectAcceptanceDetails {
  const pre = kind === 'pre-acceptance'
  return {
    standard: '按双方签订的技术协议及约定要求进行验收。',
    explanation: pre
      ? '预验收单签订完成后，视为需方认可供方项目完成情况，完成项目的预验收，供方具备将产线发往需方现场的条件，供方可将此验收单用于后续的商务流程。'
      : '终验收单签订完成后，视为需方认可供方项目完成情况，完成项目的最终验收，需方可将产线用于产品的订单生产，供方可将此验收单用于后续的商务流程。',
    terms: pre
      ? 'PAT 必须在阿普顿的工厂完成，对产线的主要功能、布局进行确认。若因需方原因无法在阿普顿工厂进行验收而造成在需方现场出现不影响主要功能的问题，因更改可能延长调试时间和终验收时间的，阿普顿有权拒绝修改，争议问题双方协商。'
      : 'FAT 在客户工厂完成，需按照技术协议执行。FAT 需在设备到达需方工厂的 3 个月内完成，因需方原因 3 个月内无法完成 FAT 的视为通过 FAT。若需方已将设备用于订单生产且时间超过 1 周，也视为客户通过 FAT。',
    passed: null,
  }
}

export interface ProjectFormAttachment {
  id: string
  fileName: string
  content: string | null
}

export interface ProjectSampleItem {
  materialName: string
  quantity: number | null
  unit: string
  requiredOn: string | null
  phase: string
  note: string
  image: ProjectFormAttachment | null
}

export interface ProjectFileFormData {
  recipient?: string | null
  contact?: string | null
  address?: string | null
  items?: ProjectSampleItem[] | null
  supplier?: string | null
  customer?: string | null
  changedOn?: string | null
  reason?: string | null
  before?: string | null
  after?: string | null
  paid?: boolean | null
  note?: string | null
  evidence?: ProjectFormAttachment[] | null
  acceptance?: ProjectAcceptanceDetails | null
}

export interface ProjectFileFormSummary {
  id: string
  number: string
  rowVersion: number
  archivedRevision: number
  archivedLayoutVersion?: number
  fileId: string | null
  fileVersion: number
  evidenceFileId: string | null
  updatedAt: string
}

export interface ProjectFileForm {
  summary: ProjectFileFormSummary
  data: ProjectFileFormData
}

export interface ProjectFileFormIndex {
  receiving?: { recipient?: string | null; contact?: string | null; address?: string | null } | null
  folderId: string
  projectCode: string
  projectName: string
  company: string
  customer: string
  canEdit: boolean
  canArchive: boolean
  canExport?: boolean
  forms: ProjectFileFormSummary[]
}

export const fileFormKinds: { kind: ProjectFileFormKind; button: string; title: string }[] = [
  { kind: 'sample-demand', button: '样件需求', title: '样件需求计划' },
  { kind: 'customer-change', button: '客户需求变更单', title: '客户需求变更单' },
  { kind: 'pre-acceptance', button: '预验收单', title: '预验收单' },
  { kind: 'final-acceptance', button: '终验收单', title: '终验收单' },
]
