export interface OplAttachment { id: string; name: string; length: number }
export interface OplIssue {
  id: string; projectId: string; number: number; description: string; priority: string; status: string
  owner: string; dueDate: string | null; solution: string; createdBy: string; createdAt: string
  createdByDisplayName?: string | null
  completedAt: string | null; updatedBy: string; updatedAt: string; rowVersion: number; attachments: OplAttachment[]; relatedProjectId?: string | null
  notification?: { sent: boolean; message: string }
}
export interface OplRow { issue: OplIssue; canEdit: boolean; canComplete: boolean }
export interface OplProject { id: string; code: string; name: string }
export interface OplView { rows: OplRow[]; people: Array<{ username: string; displayName: string }>; rootProject?: OplProject; subprojects?: OplProject[] }
export interface OplInput {
  description: string; priority: string; status: string; owner: string; dueDate: string | null
  solution: string; expectedRowVersion: number; attachmentIds: string[]; relatedProjectId: string | null
  notifyGroup?: boolean
}
