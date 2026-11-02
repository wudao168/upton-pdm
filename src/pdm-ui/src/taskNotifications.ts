import type { UserNotification } from './types'

/** 工时催填由工时管理入口提醒；更正通知等普通业务消息仍保留。 */
export function isTaskCenterNotification(notification: Pick<UserNotification, 'category'>) {
  return notification.category !== 'work-hours'
}
