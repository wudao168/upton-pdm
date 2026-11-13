import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { projectCenterAccessKey, projectCenterSchema } from '../src/projectCenterAccess'
import ReleaseOverview from '../src/components/ReleaseOverview.vue'
import { releaseFailureMessage } from '../src/releaseFailureMessage'
import type { ManufacturingBomBaseline, ReleasePackageSummary, ReleasePreviewItemResult } from '../src/types'

const api = vi.hoisted(() => ({
  listReleasePreviewItems: vi.fn(),
}))
vi.mock('../src/api', () => api)

function previewItem(overrides: Partial<ReleasePreviewItemResult> = {}): ReleasePreviewItemResult {
  return {
    releasePackageId: 'release-published', releasePackageNumber: 'RP-N-002', documentId: 'doc-1',
    drawingNumber: '7080113.00-01', fileName: '7080113.00-01.SLDPRT', kind: 'Part', format: 'Step',
    succeeded: true, fileLength: 301975, error: null, ...overrides,
  }
}

function publishedRelease(overrides: Partial<ReleasePackageSummary> = {}): ReleasePackageSummary {
  return {
    id: 'release-published', number: 'RP-N-002', state: '已发布', scope: 'NonStandardWithDrawing', workflowVersion: 1,
    steps: [], selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: true,
    standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], previewState: 'Succeeded', ...overrides,
  }
}

describe('ReleaseOverview', () => {
  beforeEach(() => { vi.clearAllMocks() })

  it('转图完成但实际投放失败时发出项目异常，明细恢复后清除', async () => {
    api.listReleasePreviewItems.mockResolvedValueOnce([previewItem({ succeeded: false, error: '生产目录尚未投放' })])
    const release = { id: 'release-1', number: 'RP-N-002', state: '已发布', scope: 'NonStandardSupplement', locksDocuments: true, previewState: 'Succeeded', steps: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [] } as unknown as ReleasePackageSummary
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [release], versions: [], baselines: [], projectId: 'project-1', token: 'token' } })
    await flushPromises()
    expect(wrapper.emitted('previewAttention')).toEqual([['project-1', true]])
    api.listReleasePreviewItems.mockResolvedValueOnce([previewItem()])
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(wrapper.emitted('previewAttention')).toEqual([['project-1', true], ['project-1', false]])
    wrapper.unmount()
  })

  it('已发布的非标失败包直接显示重试按钮，点击只重试对应发布包', async () => {
    const packages = ['a', 'b'].map(id => ({ id, number: `RP-${id}`, state: '已发布', scope: 'NonStandardSupplement', locksDocuments: true, previewState: 'Failed', previewAttempts: 5, previewError: '转图失败原因', steps: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [] } as unknown as ReleasePackageSummary))
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: packages, versions: [], baselines: [], canManageRelease: true } })
    expect(wrapper.get('[role="alert"]').text()).toContain('2 个非标件发布包')
    const target = wrapper.get('[data-release-package-id="b"]')
    expect(target.text()).toContain('自动重试已暂停')
    await target.findAll('button').find(button => button.text() === '重试转图')!.trigger('click')
    expect(wrapper.emitted('retryPreview')).toEqual([['b']])
    expect(wrapper.emitted('open')).toBeUndefined()
    await wrapper.setProps({ pending: true })
    expect(target.findAll('button').find(button => button.text() === '正在提交…')!.attributes('disabled')).toBeDefined()
    await wrapper.setProps({ canManageRelease: false })
    expect(wrapper.findAll('.pdm-release-retry-conversion')).toHaveLength(0)
    wrapper.unmount()
  })

  it('标准和电气历史异常状态不显示任何转图提示，较早的非标失败包仍可直接处理', () => {
    const base = { state: '已发布', locksDocuments: true, previewState: 'Pending', previewAttempts: 5, steps: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [] }
    const packages = [ { ...base, id: 'standard', number: 'RP-S', scope: 'StandardFormal' }, { ...base, id: 'electrical', number: 'RP-E', scope: 'ElectricalFormal' },
      ...Array.from({ length: 6 }, (_, index) => ({ ...base, id: `n-${index}`, number: `RP-N-${index}`, scope: 'NonStandardSupplement', createdAt: String(6 - index), previewState: index === 5 ? 'Failed' : 'Succeeded' })) ] as unknown as ReleasePackageSummary[]
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: packages, versions: [], baselines: [], canManageRelease: true } })
    const cards = wrapper.findAll('.pdm-release-stream-card')
    expect(cards[0]!.text()).not.toContain('转图')
    expect(cards[2]!.text()).not.toContain('转图')
    expect(wrapper.get('[data-release-package-id="n-5"]').text()).toContain('重试转图')
    wrapper.unmount()
  })

  it('只读取一次初始明细，相同发布状态和顺序变化不触发重复刷新', async () => {
    const releases = ['a', 'b'].map(id => ({ id, number: id, state: '已发布', scope: 'NonStandardWithDrawing', previewState: 'Succeeded', previewUpdatedAt: '2026-10-27T08:00:00Z', steps: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [] } as unknown as ReleasePackageSummary))
    api.listReleasePreviewItems.mockResolvedValue([previewItem({ releasePackageId: 'a' })])
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: releases, versions: [], baselines: [], projectId: 'project-1', token: 'token' } })
    await flushPromises()
    expect(api.listReleasePreviewItems).toHaveBeenCalledTimes(1)
    for (let index = 0; index < 4; index++) {
      await wrapper.setProps({ releasePackages: [...releases].reverse().map(release => ({ ...release })) })
      await flushPromises()
    }
    expect(api.listReleasePreviewItems).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  it('状态变化和手动刷新保留现有汇总表，不切换为整块加载提示', async () => {
    const release = { id: 'release-published', number: 'RP-N-002', state: '已发布', scope: 'NonStandardWithDrawing', previewState: 'Running', steps: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [] } as unknown as ReleasePackageSummary
    api.listReleasePreviewItems.mockResolvedValue([previewItem()])
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [release], versions: [], baselines: [], projectId: 'project-1', token: 'token' } })
    await flushPromises()
    const table = wrapper.get('.pdm-release-preview-table').element
    let complete!: (items: ReleasePreviewItemResult[]) => void
    api.listReleasePreviewItems.mockReturnValueOnce(new Promise(resolve => { complete = resolve }))
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Succeeded' }] })
    expect(api.listReleasePreviewItems).toHaveBeenCalledTimes(2)
    expect(wrapper.get('.pdm-release-preview-table').element).toBe(table)
    expect(wrapper.text()).not.toContain('正在读取图纸转出状态…')
    complete([previewItem()])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(api.listReleasePreviewItems).toHaveBeenCalledTimes(3)
    expect(wrapper.get('.pdm-release-preview-table').element).toBe(table)
    wrapper.unmount()
  })

  it('切换项目后丢弃上一项目晚返回的明细', async () => {
    let completePrevious!: (items: ReleasePreviewItemResult[]) => void
    api.listReleasePreviewItems.mockReturnValueOnce(new Promise(resolve => { completePrevious = resolve }))
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [publishedRelease()], versions: [], baselines: [], projectId: 'previous', token: 'token' } })
    api.listReleasePreviewItems.mockResolvedValueOnce([previewItem()])
    await wrapper.setProps({ projectId: 'current' })
    await flushPromises()
    completePrevious([previewItem({ succeeded: false }), previewItem({ documentId: 'doc-2' })])
    await flushPromises()
    expect(wrapper.findAll('.pdm-release-preview-table tbody td').map(cell => cell.text())).toEqual(['RP-N-002', '1', '1 / 1（100%）', '已完成', '—', '—'])
    wrapper.unmount()
  })

  it('shows a Chinese explanation for an unmapped technical release failure', () => {
    expect(releaseFailureMessage('MySqlException: connection timed out')).toBe('操作未完成，请重试；如仍失败，请联系管理员。')
    expect(releaseFailureMessage('发布失败：MySqlException: connection timed out')).toBe('操作未完成，请重试；如仍失败，请联系管理员。')
  })

  it('区分转换中、排队中和失败', async () => {
    const release = publishedRelease({ previewState: 'Running' })
    api.listReleasePreviewItems.mockResolvedValue([previewItem({ succeeded: false, fileLength: 0, error: '正在转换…' })])
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [release], versions: [], baselines: [], projectId: 'p', token: 't', canManageRelease: true } })
    await flushPromises()
    expect(wrapper.get('.pdm-release-preview-pending').text()).toBe('转换中')
    expect(wrapper.get('progress').attributes('value')).toBe('0')
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Pending' }] })
    await flushPromises()
    expect(wrapper.get('.pdm-release-preview-pending').text()).toBe('排队中')
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Failed' }] })
    await flushPromises()
    expect(wrapper.get('.pdm-release-preview-failed').text()).toBe('有失败项')
    expect(wrapper.get('.pdm-release-preview-list header small').text()).toContain('失败 1')
    wrapper.unmount()
  })

  it('explains a publishing conversion blocker and clears the old reason when conversion recovers', async () => {
    const release: ReleasePackageSummary = { id: 'waiting', number: 'RP-WAITING', state: '发布中', scope: 'NonStandardWithDrawing',
      workflowVersion: 1, steps: [], selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], previewState: 'Pending',
      previewError: 'COM error 80080005 (CO_E_SERVER_EXEC_FAILURE)', previewUpdatedAt: '2026-10-09T00:15:41Z' }
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [release], versions: [], baselines: [], canManageRelease: true } })
    await wrapper.get('.pdm-release-retry-conversion').trigger('click')
    expect(wrapper.emitted('retryPreview')).toEqual([['waiting']])
    expect(wrapper.emitted('open')).toBeUndefined()
    expect(wrapper.get('.pdm-release-retry-feedback').text()).toContain('每5秒自动读取')
    await wrapper.setProps({ pending: true })
    expect(wrapper.get('.pdm-release-retry-feedback').text()).toBe('正在提交重试请求…')
    expect(wrapper.get<HTMLButtonElement>('.pdm-release-retry-conversion').element.disabled).toBe(true)
    await wrapper.setProps({ canManageRelease: false })
    expect(wrapper.find('.pdm-release-retry-conversion').exists()).toBe(false)
    expect(wrapper.get('.pdm-release-publishing-reason').text()).toContain('当前环节：等待图纸转换')
    expect(wrapper.get('.pdm-release-publishing-reason').text()).toContain('转图服务器无法启动 SolidWorks（错误码 80080005）')
    expect(wrapper.get('.pdm-release-publishing-reason').text()).toContain('最近转图状态更新')
    await wrapper.setProps({ canManageRelease: true, pending: false, releasePackages: [{ ...release, previewState: 'Failed' }] })
    expect(wrapper.get('.pdm-release-publishing-reason').text()).toContain('自动重试已暂停')
    expect(wrapper.get('.pdm-release-publishing-reason').text()).toContain('无需重新审批')
    expect(wrapper.get('.pdm-release-publishing-reason').text()).not.toContain('图纸转换中')
    expect(wrapper.get('.pdm-release-overview-active em.is-error').text()).toBe('转图失败')
    expect(wrapper.get<HTMLButtonElement>('.pdm-release-retry-conversion').element.disabled).toBe(false)
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Running', previewError: null }] })
    expect(wrapper.get('.pdm-release-publishing-reason').text()).toContain('图纸转换中')
    expect(wrapper.text()).not.toContain('80080005')
    await wrapper.setProps({ releasePackages: [{ ...release, state: '已发布', previewState: 'Succeeded', previewError: null }] })
    expect(wrapper.find('.pdm-release-publishing-reason').exists()).toBe(false)
    expect(wrapper.get('.pdm-release-published-state').text()).toBe('已发布')
    wrapper.unmount()
  })

  it('shows the publishing failure reason and an explicit published state independently of conversion', async () => {
    const base: ReleasePackageSummary = { id: 'failed', number: 'RP-FAILED', state: '发布失败', scope: 'NonStandardWithDrawing',
      workflowVersion: 1, steps: [], selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], publishError: 'Deadlock found when trying to get lock; try restarting transaction' }
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [base, { ...base, id: 'success', number: 'RP-OK',
      scope: 'StandardFormal', state: '已发布', publishError: undefined }], versions: [], baselines: [] } })
    expect(wrapper.get('.pdm-release-failure-reason').text()).toContain('数据库事务发生死锁，本次发布被中止')
    expect(wrapper.get('.pdm-release-failure-reason').text()).not.toContain('Deadlock')
    expect(wrapper.get('.pdm-release-published-state').text()).toBe('已发布')
    await wrapper.get('.pdm-release-overview-active button').trigger('click')
    expect(wrapper.emitted('open')).toEqual([['failed']])
    await wrapper.setProps({ releasePackages: [{ ...base, publishError: ' ' }] })
    expect(wrapper.get('.pdm-release-failure-reason').text()).toContain('服务端未返回失败原因')
    wrapper.unmount()
  })

  it('is read-only and routes a package back to its BOM page', async () => {
    const release: ReleasePackageSummary = {
      id: 'release-1', number: 'RP-S-001', state: '审批中', steps: [], scope: 'StandardFormal', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-08-21T08:00:00Z',
    }
    const baseline: ManufacturingBomBaseline = {
      id: 'baseline-1', projectId: 'project-1', sequence: 1, label: 'BL-001',
      standardBomVersionId: 'standard-1', nonStandardBomVersionId: 'nonstandard-1', electricalBomVersionId: 'electrical-1',
      changeNumber: 'RP-S-001', changeReason: '首次发布', effectiveSerialFrom: '70000001', releasePackageId: 'release-1',
      createdBy: 'admin', createdAt: '2026-08-21T08:00:00Z',
    }
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [release], versions: [], baselines: [baseline] } })

    expect(wrapper.find('.pdm-release-overview-header').exists()).toBe(false)
    expect(wrapper.findAll('.pdm-release-stream-card')).toHaveLength(3)
    expect(wrapper.text()).not.toContain('历史组合发布')
    expect(wrapper.text()).not.toContain('创建草稿')
    expect(wrapper.find('.pdm-release-baseline-list').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('生效序列号')
    expect(wrapper.text()).not.toContain('70000001')
    await wrapper.findAll('button').find(button => button.text().includes('RP-S-001'))!.trigger('click')
    expect(wrapper.emitted('open')).toEqual([['release-1']])
  })

  it('为转出不全的发布包显示原因并调用已有的包级重试', async () => {
    const release = publishedRelease()
    api.listReleasePreviewItems.mockResolvedValue([previewItem(), previewItem({ documentId: 'doc-2', succeeded: false, error: '尚未生成预览。' })])
    const permissions = ref([projectCenterSchema, 'project.content.write'])
    const wrapper = mount(ReleaseOverview, {
      props: { releasePackages: [release], versions: [], baselines: [], projectId: 'p', token: 't', canManageRelease: true },
      global: { provide: { [projectCenterAccessKey as symbol]: permissions } },
    })
    await flushPromises()
    const panel = wrapper.get('.pdm-release-preview-list')
    expect(panel.findAll('tbody td').map(cell => cell.text())).toEqual(['RP-N-002', '2', '1 / 2（50%）', '转出不全', '尚未生成预览。', '重试'])
    expect(panel.get('progress').attributes()).toMatchObject({ value: '1', max: '2' })
    expect(panel.findAll('button').map(button => button.text())).toEqual(['刷新', '重试'])
    expect(panel.findAll('input, a')).toHaveLength(0)
    await panel.get('button[aria-label="重试转图 RP-N-002"]').trigger('click')
    expect(wrapper.emitted('retryPreview')).toEqual([[release.id]])
    await wrapper.setProps({ pending: true })
    expect(panel.get('button[aria-label="重试转图 RP-N-002"]').attributes('disabled')).toBeDefined()
    await wrapper.setProps({ pending: false, canManageRelease: false })
    expect(panel.find('button[aria-label="重试转图 RP-N-002"]').exists()).toBe(false)
    await wrapper.setProps({ canManageRelease: true })
    permissions.value = [projectCenterSchema]
    await flushPromises()
    expect(panel.find('button[aria-label="重试转图 RP-N-002"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('只显示运行中或需要重新发布的原因，不提供重复转图入口', async () => {
    const release = publishedRelease({ previewState: 'Running' })
    api.listReleasePreviewItems.mockResolvedValue([previewItem({ succeeded: false, error: '正在转换…' })])
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [release], versions: [], baselines: [], projectId: 'p', token: 't', canManageRelease: true } })
    await flushPromises()
    const panel = wrapper.get('.pdm-release-preview-list')
    expect(panel.get('.pdm-release-preview-problem').text()).toBe('—')
    expect(panel.find('button[aria-label^="重试转图"]').exists()).toBe(false)
    api.listReleasePreviewItems.mockResolvedValue([previewItem({ succeeded: false, error: '该图档没有随本次发布转为正式版本，需要重新发布后才能转出。' })])
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Failed' }] })
    await flushPromises()
    expect(panel.get('.pdm-release-preview-problem').text()).toContain('需要重新发布')
    expect(panel.find('button[aria-label^="重试转图"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('filters conversion details by release package', async () => {
    const first: ReleasePackageSummary = {
      id: 'pkg-a', number: 'RP-A', state: '已发布', steps: [], scope: 'NonStandardWithDrawing', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-09-20T08:00:00Z',
      previewState: 'Succeeded',
    }
    const second: ReleasePackageSummary = { ...first, id: 'pkg-b', number: 'RP-B', previewState: 'Failed' }
    api.listReleasePreviewItems.mockResolvedValue([
      previewItem({ releasePackageId: 'pkg-a', releasePackageNumber: 'RP-A', documentId: 'doc-a', drawingNumber: 'A-001' }),
      previewItem({ releasePackageId: 'pkg-b', releasePackageNumber: 'RP-B', documentId: 'doc-b', drawingNumber: 'B-001', succeeded: false, error: '失败原因' }),
    ])
    const wrapper = mount(ReleaseOverview, {
      props: { releasePackages: [first, second], versions: [], baselines: [], projectId: 'project-1', token: 'token', canManageRelease: true },
    })
    await flushPromises()
    expect(wrapper.findAll('.pdm-release-preview-table tbody td:first-child').map(cell => cell.text())).toEqual(['RP-A', 'RP-B'])
    expect(wrapper.find('select[aria-label="按最新BOM筛选"]').exists()).toBe(false)
    await wrapper.get('select[aria-label="按发布包筛选"]').setValue('pkg-b')
    expect(wrapper.findAll('.pdm-release-preview-table tbody td:first-child').map(cell => cell.text())).toEqual(['RP-B'])
  })

  it('still shows the change number when the package has its own one', () => {
    const supplement: ReleasePackageSummary = {
      id: 'release-supplement', number: 'RP-N-005', changeNumber: 'ECN-N-005', state: '已发布', steps: [],
      scope: 'NonStandardSupplement', workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false,
      locksDocuments: true, standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      createdAt: '2026-09-20T09:00:00Z', previewState: 'Succeeded',
    }
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [supplement], versions: [], baselines: [] } })

    expect(wrapper.get('.pdm-release-package-description>span').text()).toBe('RP-N-005')
    expect(wrapper.get('.pdm-release-package-description>small').text()).toBe('增补/变更 · ECN-N-005')
    expect(wrapper.get('.pdm-release-row-tail em').text()).toBe('转图完成')
  })
  it('铭牌记录合并到非标卡片，保留全部铭牌历史、最近五项BOM及原详情入口', async () => {
    const make = (id: string, scope: ReleasePackageSummary['scope'], state: string, createdAt: string) => ({
      id, number: id, scope, state, createdAt, steps: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
    } as unknown as ReleasePackageSummary)
    const packages = [
      make('NP-OLD', 'Nameplate', '已发布', '2026-09-01'),
      make('NP-ACTIVE', 'Nameplate', '待审核', '2026-11-06'),
      ...Array.from({ length: 6 }, (_, index) => make('BOM-' + index, 'NonStandardWithDrawing', '已发布', '2026-11-0' + (6 - index))),
    ]
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: packages, versions: [], baselines: [] } })
    const cards = wrapper.findAll('.pdm-release-stream-card')
    expect(cards).toHaveLength(3)
    const nonStandard = cards[1]!
    expect(nonStandard.text()).toContain('非标件BOM + 图纸')
    expect(nonStandard.text()).toContain('铭牌机加件')
    expect(nonStandard.text()).toContain('在途 1')
    expect(nonStandard.text()).toContain('NP-ACTIVE')
    expect(nonStandard.text()).toContain('NP-OLD')
    for (let index = 0; index < 5; index++) expect(nonStandard.text()).toContain('BOM-' + index)
    expect(nonStandard.text()).not.toContain('BOM-5')
    expect(cards[0]!.text()).not.toContain('NP-OLD')
    expect(cards[2]!.text()).not.toContain('NP-OLD')
    await nonStandard.findAll('button').find(button => button.text().includes('NP-OLD'))!.trigger('click')
    expect(wrapper.emitted('open')).toEqual([['NP-OLD']])
    await nonStandard.findAll('button').find(button => button.text().includes('NP-ACTIVE'))!.trigger('click')
    expect(wrapper.emitted('open')).toEqual([['NP-OLD'], ['NP-ACTIVE']])
    expect(packages[0]!.scope).toBe('Nameplate')
    wrapper.unmount()
  })

})
