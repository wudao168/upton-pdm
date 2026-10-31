import { expect, it } from 'vitest'
import { projectReleaseAttention, requiresReleasePreview, releasePreviewUnfinished, releasePreviewNeedsAttention, releasePreviewStateLabel, releasePreviewStateTag } from '../src/releasePreviewState'
import type { ReleasePackageSummary } from '../src/types'

const release = (scope = 'NonStandardSupplement', previewState = 'Pending', previewAttempts = 0) => ({ scope, previewState, previewAttempts, locksDocuments: true } as ReleasePackageSummary)

it.each(['StandardFormal', 'StandardSupplement', 'StandardLongLead', 'ElectricalFormal', 'ElectricalSupplement', 'ElectricalLongLead'])('%s 完全不产生转图标签或重试提醒', scope => {
  const old = release(scope, 'Failed', 5)
  expect(requiresReleasePreview(old)).toBe(false)
  expect(releasePreviewNeedsAttention(old)).toBe(false)
  expect(releasePreviewStateLabel(old)).toBe('')
  expect(releasePreviewStateTag(old)).toBe('')
})

it('巡检误留Pending但次数耗尽时按暂停处理，不再承诺后台自动重试', () => {
  const paused = release('NonStandardSupplement', 'Pending', 5)
  expect(releasePreviewNeedsAttention(paused)).toBe(true)
  expect(releasePreviewStateLabel(paused)).toContain('自动重试已暂停')
  expect(releasePreviewStateTag(paused)).toBe('转图失败')
})

it('服务器不可用时说明等待且不消耗次数，恢复排队后显示实际重试规则', () => {
  expect(releasePreviewStateLabel(release('NonStandardSupplement', 'WaitingForServer', 2))).toContain('不消耗失败次数')
  expect(releasePreviewStateTag(release('NonStandardSupplement', 'WaitingForServer', 2))).toBe('等待服务器')
  expect(releasePreviewStateLabel(release())).toContain('每3分钟自动重试')
})


it.each(['Pending', 'WaitingForServer', 'Running', 'Failed'])('已发布非标包的%s状态持续提醒，完成、草稿和其他BOM不提醒', state => {
  const unfinished = { ...release('NonStandardSupplement', state), state: '已发布' }
  expect(releasePreviewUnfinished(unfinished)).toBe(true)
  expect(releasePreviewUnfinished({ ...unfinished, previewState: 'Succeeded' })).toBe(false)
  expect(releasePreviewUnfinished({ ...unfinished, previewState: 'None' })).toBe(false)
  expect(releasePreviewUnfinished({ ...unfinished, state: '草稿' })).toBe(false)
  expect(releasePreviewUnfinished({ ...unfinished, scope: 'StandardFormal' })).toBe(false)
  expect(releasePreviewUnfinished({ ...unfinished, scope: 'ElectricalFormal' })).toBe(false)
})


it('新发布绿灯以正式发布时间计算，24小时到期、草稿和重试不延长，异常优先', () => {
  const now = Date.parse('2026-10-27T22:00:00+08:00')
  const project = { latestPurchasedBomPublishedAt: new Date(now - 1000).toISOString() }
  expect(projectReleaseAttention(project, undefined, false, now)).toBe('new')
  expect(projectReleaseAttention({ hasReleaseAttention: true, ...project }, undefined, false, now)).toBe('error')
  expect(projectReleaseAttention({ hasReleaseAttention: true }, [], false, now)).toBe('error')
  expect(projectReleaseAttention(project, [], true, now)).toBe('error')
  expect(projectReleaseAttention({ latestPurchasedBomPublishedAt: new Date(now - 86400000).toISOString() }, [], false, now)).toBe('')
  expect(projectReleaseAttention({ latestPurchasedBomPublishedAt: new Date(now + 1000).toISOString() }, [], false, now)).toBe('')
  expect(projectReleaseAttention({}, [{ ...release(), state: '草稿', publishedAt: new Date(now).toISOString() }], false, now)).toBe('')
  expect(projectReleaseAttention({}, [{ ...release('StandardFormal', 'Succeeded'), state: '已发布', publishedAt: new Date(now).toISOString() }], false, now)).toBe('new')
  expect(projectReleaseAttention(project, [{ ...release('ElectricalFormal'), state: '发布失败' }], false, now)).toBe('error')
  expect(projectReleaseAttention({}, [], false, now)).toBe('')
})

it('非标绿灯不按24小时清除，实际供应链接收后停止，红色异常仍优先', () => {
  const now = Date.parse('2026-10-28T12:00:00+08:00')
  const nonStandard = { ...release('NonStandardSupplement', 'Succeeded'), state: '已发布', publishedAt: '2026-10-20T12:00:00+08:00' }
  expect(projectReleaseAttention({ hasUnreceivedNonStandardRelease: true }, [nonStandard], false, now)).toBe('new')
  expect(projectReleaseAttention({ hasUnreceivedNonStandardRelease: true }, [nonStandard], false, now + 5 * 86400000)).toBe('new')
  expect(projectReleaseAttention({ hasUnreceivedNonStandardRelease: false }, [nonStandard], false, now)).toBe('')
  expect(projectReleaseAttention({ hasUnreceivedNonStandardRelease: false }, [{ ...nonStandard, publishedAt: new Date(now).toISOString() }], false, now)).toBe('')
  expect(projectReleaseAttention({ hasUnreceivedNonStandardRelease: true, hasReleaseAttention: true }, [nonStandard], false, now)).toBe('error')
})
