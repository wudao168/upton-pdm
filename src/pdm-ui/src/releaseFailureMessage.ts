import type { ReleasePackageSummary } from './types'
import { userErrorMessage } from './userErrorMessage'

export function releaseFailureMessage(error?: string | null): string {
  const message = error?.trim() ?? ''
  if (/80080005|CO_E_SERVER_EXEC_FAILURE/i.test(message)) {
    return '转图服务器无法启动 SolidWorks（错误码 80080005）。请管理员检查转图服务器上的 SolidWorks 启动状态，恢复后重试。'
  }
  if (/800706BA|RPC.*(unavailable|不可用)/i.test(message)) {
    return '转图服务器与 SolidWorks 的连接已中断（错误码 800706BA）。请检查转图电脑上的 SolidWorks 状态，恢复后重试。'
  }
  if (/Deadlock found when trying to get lock/i.test(message)) {
    return '数据库事务发生死锁，本次发布被中止。请稍后重试；如反复出现，请联系管理员排查。'
  }
  return message ? userErrorMessage(message) : '服务端未返回失败原因，请打开发布单查看详情。'
}

export function releasePublishingMessage(release: ReleasePackageSummary): string {
  if (release.state !== '发布中') return ''
  const phase = release.previewState === 'Failed' ? '图纸转换失败，自动重试已暂停。恢复转图服务器后请重试转图，无需重新审批。'
    : release.previewState === 'Pending' ? '当前环节：等待图纸转换。'
    : release.previewState === 'Running' ? '当前环节：图纸转换中，正在生成正式图纸及发布资料。'
      : '当前状态：后台发布处理中，服务端尚未返回具体环节。'
  const error = release.previewError?.trim()
  return error ? `${phase}最近一次转图错误：${releaseFailureMessage(error)}` : phase
}
