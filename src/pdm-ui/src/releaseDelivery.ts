export interface ReleaseNotificationRule {
  id: string; projectId: string | null; bomKind: string; event: string; departmentIds: string[]
  includeChildren: boolean; channels: string[]; dingTalkGroupId: string | null; requireExecution: boolean
}
export interface ReleaseVersionBinding { documentId: string; versionId: string; fileName: string; revision: string; sha256: string; publish: boolean; requiresPromotion?: boolean; previousReleasedRevision?: string; targetRevision?: string }
export interface ReleaseDepartmentReceipt {
  id: string; departmentId: string; departmentName: string; event: string; requireExecution: boolean
  recipients: Array<{ username: string }>; receivedBy?: string | null; receivedAt?: string | null
  executedBy?: string | null; executedAt?: string | null; executionNote?: string | null
}
export interface ReleaseChannelDelivery {
  id: string; event: string; channel: string; destination: string; departmentIds: string[]; state: string
  attempts: number; message?: string | null; sentAt?: string | null; nextAttemptAt?: string | null
}
export interface ReleaseExecutionView {
  id: string; state: string; ready: boolean; acknowledgementIds: string[]
  workflowNotifications?: Array<{ state: string; message: string; enabled: boolean }>
  selectedBomItemIds: string[]; confirmedRemovedBomItemIds: string[]; versionBindings?: ReleaseVersionBinding[] | null
  progress: null | {
    u9: Record<string, { state: string; message: string; confirmedAt?: string | null }>
    requiresU9?: boolean
    previewRequired: boolean; previewState: string; previewMessage?: string | null
    departments: ReleaseDepartmentReceipt[]; deliveries: ReleaseChannelDelivery[]; notificationRules: ReleaseNotificationRule[]
  }
}
export const notificationEvents = { Published: '首次正式发布', Changed: '增补/变更发布', Ready: 'U9及生产资料齐套' }
export const notificationChannels = { InApp: '站内消息', DingTalk: '钉钉', Email: '邮件' }
