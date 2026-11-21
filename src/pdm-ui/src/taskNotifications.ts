import type { MyApprovalTask, UserNotification } from './types'

export const releaseDrawingReworkCategory = 'ReleaseDrawingChangesRequested'
export const releaseDrawingResubmittedCategory = 'ReleaseDrawingResubmitted'
export function releaseDrawingReworkDocumentId(notification: UserNotification) {
  if (notification.category !== releaseDrawingReworkCategory && notification.category !== releaseDrawingResubmittedCategory) return undefined
  const id = notification.sourceKey.split(':')[1]
  return id?.match(/^([0-9a-f]{8})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{4})([0-9a-f]{12})$/i)?.slice(1).join('-')
}

export const oplResponseCategory = 'OplResponsePending'
export function oplResponseIssueId(notification: UserNotification) {
  return notification.category === oplResponseCategory ? notification.sourceKey.split(':')[1] : undefined
}

export function isProjectTodo(notification: UserNotification) {
  return notification.category === 'ProjectTodo' || notification.category === oplResponseCategory
}

/** 工时催填由工时管理入口提醒；更正通知等普通业务消息仍保留。 */
export function isTaskCenterNotification(notification: Pick<UserNotification, 'category'>) {
  return notification.category !== 'work-hours'
}

/** 发布审批通知与当前待办共用一行，退回等结果通知仍独立保留。 */
export function releaseApprovalTaskForNotification(notification: UserNotification, tasks: MyApprovalTask[]) {
  const resubmitted = notification.category === releaseDrawingResubmittedCategory
  if (!(resubmitted || notification.category === 'ReleaseApprovalPending') || !notification.releasePackageId
    || notification.resolvedAt || notification.isSenderReceipt) return undefined
  const taskId = resubmitted ? notification.sourceKey.split(':')[4] : undefined
  return tasks.find(task => task.kind !== 'validationPlan' && task.releasePackageId === notification.releasePackageId
    && task.projectId === notification.projectId && (!resubmitted || task.id.replaceAll('-', '').toLowerCase() === taskId))
}
