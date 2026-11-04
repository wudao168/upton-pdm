import type { MyApprovalTask, UserNotification } from './types'

/** 工时催填由工时管理入口提醒；更正通知等普通业务消息仍保留。 */
export function isTaskCenterNotification(notification: Pick<UserNotification, 'category'>) {
  return notification.category !== 'work-hours'
}

/** 发布审批通知与当前待办共用一行，退回等结果通知仍独立保留。 */
export function releaseApprovalTaskForNotification(notification: UserNotification, tasks: MyApprovalTask[]) {
  if (notification.category !== 'ReleaseApprovalPending' || !notification.releasePackageId || notification.resolvedAt || notification.isSenderReceipt) return undefined
  return tasks.find(task => task.kind !== 'validationPlan' && task.releasePackageId === notification.releasePackageId
    && task.projectId === notification.projectId)
}
