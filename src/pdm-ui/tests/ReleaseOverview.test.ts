import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ReleaseOverview from '../src/components/ReleaseOverview.vue'
import { releaseFailureMessage } from '../src/releaseFailureMessage'
import type { BomVersion, ManufacturingBomBaseline, ReleasePackageSummary, ReleasePreviewItemResult } from '../src/types'

const api = vi.hoisted(() => ({
  listReleasePreviewItems: vi.fn(),
  retryReleasePreviewItem: vi.fn(),
  retryReleasePreviewItems: vi.fn(),
  downloadReleasePreviewArchive: vi.fn(),
}))
vi.mock('../src/api', () => api)

function previewItem(overrides: Partial<ReleasePreviewItemResult> = {}): ReleasePreviewItemResult {
  return {
    releasePackageId: 'release-published', releasePackageNumber: 'RP-N-002', documentId: 'doc-1',
    drawingNumber: '7080113.00-01', fileName: '7080113.00-01.SLDPRT', kind: 'Part', format: 'Step',
    succeeded: true, fileLength: 301975, error: null, ...overrides,
  }
}

describe('ReleaseOverview', () => {
  beforeEach(() => { vi.clearAllMocks() })

  it('shows a Chinese explanation for an unmapped technical release failure', () => {
    expect(releaseFailureMessage('MySqlException: connection timed out')).toBe('操作未完成，请重试；如仍失败，请联系管理员。')
    expect(releaseFailureMessage('发布失败：MySqlException: connection timed out')).toBe('操作未完成，请重试；如仍失败，请联系管理员。')
  })

  it('does not count running or queued artifacts as failures and prevents duplicate retries', async () => {
    const release: ReleasePackageSummary = { id: 'release-published', number: 'RP-N-002', state: '已发布', scope: 'NonStandardWithDrawing',
      workflowVersion: 1, steps: [], selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], previewState: 'Running' }
    api.listReleasePreviewItems.mockResolvedValue([previewItem({ succeeded: false, fileLength: 0, error: '正在转换…' })])
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [release], versions: [], baselines: [], projectId: 'p', token: 't', canManageRelease: true } })
    await flushPromises()
    expect(wrapper.text()).toContain('转换中 1、排队中 0、失败 0')
    expect(wrapper.get('.pdm-release-preview-pending').text()).toBe('转换中')
    expect(wrapper.findAll('button').find(button => button.text() === '重试')!.element.disabled).toBe(true)
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Pending' }] })
    await flushPromises()
    expect(wrapper.text()).toContain('转换中 0、排队中 1、失败 0')
    await wrapper.setProps({ releasePackages: [{ ...release, previewState: 'Failed' }] })
    await flushPromises()
    expect(wrapper.text()).toContain('转换中 0、排队中 0、失败 1')
    wrapper.unmount()
  })

  it('explains a publishing conversion blocker and clears the old reason when conversion recovers', async () => {
    const release: ReleasePackageSummary = { id: 'waiting', number: 'RP-WAITING', state: '发布中', scope: 'NonStandardWithDrawing',
      workflowVersion: 1, steps: [], selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
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
    expect(wrapper.findAll('.pdm-release-baseline-list th').map(cell => cell.text())).toEqual(['基线', '变更单号', '生成时间'])
    expect(wrapper.text()).not.toContain('生效序列号')
    expect(wrapper.text()).not.toContain('70000001')
    await wrapper.findAll('button').find(button => button.text().includes('RP-S-001'))!.trigger('click')
    expect(wrapper.emitted('open')).toEqual([['release-1']])
  })

  it('lists conversion details with per-item retry and packaged download', async () => {
    const published: ReleasePackageSummary = {
      id: 'release-published', number: 'RP-N-002', state: '已发布', steps: [], scope: 'NonStandardWithDrawing', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: true, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-09-20T08:00:00Z',
      previewState: 'Failed', previewError: '发布包尚未进入服务器转换状态。', previewAttempts: 5,
    }
    api.listReleasePreviewItems.mockResolvedValue([
      previewItem(),
      previewItem({ documentId: 'doc-2', drawingNumber: '7080113.00-02', fileName: '7080113.00-02 工程图.SLDDRW', kind: 'Drawing', format: 'Pdf', succeeded: false, fileLength: 0, error: '转图服务器返回错误(500)：SolidWorks转换进程失败。' }),
    ])
    api.retryReleasePreviewItem.mockResolvedValue({ ok: true, item: previewItem(), message: '已重新转出该图档。' })
    api.downloadReleasePreviewArchive.mockResolvedValue(undefined)
    const wrapper = mount(ReleaseOverview, {
      props: { releasePackages: [published], versions: [], baselines: [], pending: false, projectId: 'project-1', token: 'token', canManageRelease: true },
    })
    await flushPromises()

    expect(api.listReleasePreviewItems).toHaveBeenCalledWith('project-1', undefined, 'token')
    const table = wrapper.get('.pdm-release-preview-table')
    expect(table.text()).toContain('7080113.00-01')
    expect(table.text()).toContain('7080113.00-02')
    expect(table.text()).toContain('STEP')
    expect(table.text()).toContain('PDF')
    expect(table.text()).toContain('成功')
    expect(table.text()).toContain('失败')
    expect(table.text()).toContain('转图服务器返回错误(500)')
    expect(wrapper.get('.pdm-release-preview-pager').text()).toContain('单页最多 50 条')
    expect(wrapper.get('.pdm-release-row-tail em').text()).toBe('转图失败')

    // “选中失败”一键勾选当前筛选下的全部失败项
    await wrapper.findAll('button').find(button => button.text().includes('选中失败'))!.trigger('click')
    await flushPromises()
    expect(wrapper.findAll('button').some(button => button.text().includes('重试选中（1）'))).toBe(true)

    // 失败行可以单项重试（不影响同包其它文件）
    await table.findAll('button').find(button => button.text() === '重试')!.trigger('click')
    await flushPromises()
    expect(api.retryReleasePreviewItem).toHaveBeenCalledWith('release-published', 'doc-2', 'token')

    // 勾选失败项后批量重试：同一发布包的多个图档只发一次请求（引用树只下发一次）
    api.retryReleasePreviewItems.mockResolvedValue({ ok: true, items: [previewItem({ documentId: 'doc-2', succeeded: true, error: null })], message: '已重新转出 1 项。' })
    await table.find('input[aria-label="选择 7080113.00-02"]').setValue(true)
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().includes('重试选中'))!.trigger('click')
    await flushPromises()
    expect(api.retryReleasePreviewItems).toHaveBeenCalledTimes(1)
    expect(api.retryReleasePreviewItems).toHaveBeenCalledWith('release-published', ['doc-2'], 'token')

    // 多选后“下载选中”，或“全部下载”，都以压缩包形式下载
    await table.find('input[aria-label="选择 7080113.00-01"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text().includes('下载选中'))!.trigger('click')
    await flushPromises()
    expect(api.downloadReleasePreviewArchive).toHaveBeenCalledWith('project-1', undefined, ['doc-1'], 'token')
    await wrapper.findAll('button').find(button => button.text() === '全部下载')!.trigger('click')
    await flushPromises()
    expect(api.downloadReleasePreviewArchive).toHaveBeenCalledWith('project-1', undefined, ['doc-1'], 'token')
  })

  it('disables conversion retry without release management permission', async () => {
    const published: ReleasePackageSummary = {
      id: 'release-published', number: 'RP-N-002', state: '已发布', steps: [], scope: 'NonStandardWithDrawing', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: true, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-09-20T08:00:00Z',
      previewState: 'Failed',
    }
    api.listReleasePreviewItems.mockResolvedValue([
      previewItem({ releasePackageId: 'release-published', documentId: 'doc-failed', drawingNumber: '7080113.00', succeeded: false, fileLength: 0, error: '转图服务器返回错误(500)。' }),
    ])
    const wrapper = mount(ReleaseOverview, {
      props: { releasePackages: [published], versions: [], baselines: [], projectId: 'project-1', token: 'token', canManageRelease: false },
    })
    await flushPromises()

    // 没有发布管理权限（如审图角色）时不能重试：按钮置灰并说明原因，避免直接报 403。
    const retryButton = wrapper.get('.pdm-release-preview-table button')
    expect(retryButton.text()).toBe('重试')
    expect(retryButton.attributes('disabled')).toBeDefined()
    expect(retryButton.attributes('title')).toContain('发布管理')
    const bulkRetry = wrapper.findAll('button').find(button => button.text().includes('重试选中'))!
    expect(bulkRetry.attributes('disabled')).toBeDefined()
    // 下载仍然可用（只需要项目内容查看权限）
    expect(wrapper.findAll('button').some(button => button.text() === '全部下载')).toBe(true)
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
    expect(wrapper.get('.pdm-release-preview-table').text()).toContain('A-001')
    expect(wrapper.get('.pdm-release-preview-table').text()).toContain('B-001')

    // 所有按钮常驻显示：未选择发布包时“重试整包”置灰，选择后才可点击。
    const retryPackage = wrapper.findAll('button').find(button => button.text() === '重试整包')!
    expect(retryPackage.attributes('disabled')).toBeDefined()

    await wrapper.get('select[aria-label="按发布包筛选"]').setValue('pkg-b')
    await flushPromises()
    expect(wrapper.get('.pdm-release-preview-table').text()).not.toContain('A-001')
    expect(wrapper.get('.pdm-release-preview-table').text()).toContain('B-001')
    expect(retryPackage.attributes('disabled')).toBeUndefined()
  })

  it('filters drawing conversions to the latest released BOM package and downloads every package output', async () => {
    const olderPackage: ReleasePackageSummary = {
      id: 'pkg-old', number: 'RP-N-001', state: '已发布', steps: [], scope: 'NonStandardWithDrawing', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], nonStandardBomVersionId: 'ns-v1',
      createdAt: '2026-09-20T08:00:00Z', previewState: 'Succeeded',
    }
    const currentPackage: ReleasePackageSummary = {
      ...olderPackage, id: 'pkg-current', number: 'RP-N-002', nonStandardBomVersionId: 'ns-v2',
      createdAt: '2026-09-27T08:00:00Z',
    }
    const versions: BomVersion[] = [
      {
        id: 'ns-v1', projectId: 'project-1', kind: 'NonStandard', versionNumber: 1, label: 'N-B01', state: 'Released',
        items: [{ drawingNumber: '7080113.00-01', name: '旧物料', sequence: 1, quantity: 1, unit: '件', revision: 'A', complete: true }],
        createdBy: 'admin', createdAt: '2026-09-20T08:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-20T08:00:00Z',
      },
      {
        id: 'ns-v2', projectId: 'project-1', kind: 'NonStandard', versionNumber: 2, label: 'N-B02', state: 'Released',
        items: [{ drawingNumber: '7080113.00-02', name: '当前物料', sequence: 1, quantity: 1, unit: '件', revision: 'A', complete: true }],
        createdBy: 'admin', createdAt: '2026-09-27T08:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-27T08:00:00Z',
      },
    ]
    api.listReleasePreviewItems.mockResolvedValue([
      previewItem({ releasePackageId: 'pkg-old', releasePackageNumber: 'RP-N-001', documentId: 'doc-old', drawingNumber: '7080113.00-01', fileName: '7080113.00-01.SLDPRT' }),
      previewItem({ releasePackageId: 'pkg-current', releasePackageNumber: 'RP-N-002', documentId: 'doc-current', drawingNumber: '7080113.00-02', fileName: '7080113.00-02.SLDPRT' }),
      previewItem({ releasePackageId: 'pkg-current', releasePackageNumber: 'RP-N-002', documentId: 'doc-unrelated', drawingNumber: '7080113.00-99', fileName: '7080113.00-99.SLDPRT' }),
    ])
    api.downloadReleasePreviewArchive.mockResolvedValue(undefined)
    const wrapper = mount(ReleaseOverview, {
      props: { releasePackages: [olderPackage, currentPackage], versions, baselines: [], projectId: 'project-1', token: 'token' },
    })
    await flushPromises()

    const bomFilter = wrapper.get('select[aria-label="按最新BOM筛选"]')
    expect(bomFilter.findAll('option').map(option => option.text())).toEqual(['全部', '非标件BOM · N-B02'])
    expect(wrapper.get('.pdm-release-preview-table').text()).toContain('7080113.00-01')
    expect(wrapper.get('.pdm-release-preview-table').text()).toContain('7080113.00-99')

    await bomFilter.setValue('ns-v2')
    await flushPromises()
    const filteredTable = wrapper.get('.pdm-release-preview-table')
    expect(filteredTable.text()).toContain('7080113.00-02')
    expect(filteredTable.text()).not.toContain('7080113.00-01')
    expect(filteredTable.text()).toContain('7080113.00-99')
    expect(wrapper.get('.pdm-release-preview-list header small').text()).toContain('成功 2')

    await wrapper.findAll('button').find(button => button.text() === '全部下载')!.trigger('click')
    await flushPromises()
    expect(api.downloadReleasePreviewArchive).toHaveBeenCalledWith('project-1', 'pkg-current', ['doc-current', 'doc-unrelated'], 'token')
  })

  it('matches the latest compatible package when a legacy package has no BOM version link', async () => {
    const drawing = { sequence: 1, drawingNumber: '7080113.00-02', name: '当前物料', quantity: 1, unit: '件', revision: 'A', complete: true }
    const release: ReleasePackageSummary = {
      id: 'pkg-snapshot', number: 'RP-N-003', state: '已发布', steps: [], scope: 'NonStandardWithDrawing', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], previewState: 'Succeeded',
    }
    const version: BomVersion = {
      id: 'ns-v3', projectId: 'project-1', kind: 'NonStandard', versionNumber: 3, label: 'N-B03', state: 'Released',
      items: [drawing], createdBy: 'admin', createdAt: '2026-09-28T08:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-28T08:00:00Z',
    }
    api.listReleasePreviewItems.mockResolvedValue([
      previewItem({ releasePackageId: 'pkg-snapshot', releasePackageNumber: 'RP-N-003', documentId: 'doc-current', drawingNumber: drawing.drawingNumber }),
    ])
    const wrapper = mount(ReleaseOverview, {
      props: { releasePackages: [release], versions: [version], baselines: [], projectId: 'project-1', token: 'token' },
    })
    await flushPromises()

    await wrapper.get('select[aria-label="按最新BOM筛选"]').setValue('ns-v3')
    await flushPromises()

    expect(wrapper.get('.pdm-release-preview-table').text()).toContain(drawing.drawingNumber)
    expect(wrapper.get('.pdm-release-preview-list header small').text()).toContain('成功 1')
  })

  it('still shows the change number when the package has its own one', () => {
    const supplement: ReleasePackageSummary = {
      id: 'release-supplement', number: 'RP-N-005', changeNumber: 'ECN-N-005', state: '已发布', steps: [],
      scope: 'NonStandardSupplement', workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false,
      locksDocuments: false, standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      createdAt: '2026-09-20T09:00:00Z', previewState: 'Succeeded',
    }
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [supplement], versions: [], baselines: [] } })

    expect(wrapper.get('.pdm-release-row-tail small').text()).toBe('ECN-N-005')
    expect(wrapper.get('.pdm-release-row-tail em').text()).toBe('转图完成')
  })
})
