import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ReleaseOverview from '../src/components/ReleaseOverview.vue'
import type { ManufacturingBomBaseline, ReleasePackageSummary } from '../src/types'

describe('ReleaseOverview', () => {
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
    expect(wrapper.findAll('.pdm-release-stream-card')).toHaveLength(4)
    expect(wrapper.text()).not.toContain('创建草稿')
    expect(wrapper.findAll('.pdm-release-baseline-list th').map(cell => cell.text())).toEqual(['基线', '变更单号', '生成人', '生成时间'])
    expect(wrapper.text()).not.toContain('生效序列号')
    expect(wrapper.text()).not.toContain('70000001')
    await wrapper.findAll('button').find(button => button.text().includes('RP-S-001'))!.trigger('click')
    expect(wrapper.emitted('open')).toEqual([['release-1']])
  })

  it('shows the decoupled preview (STEP/PDF) state and retries it from the stream card', async () => {
    const published: ReleasePackageSummary = {
      id: 'release-published', number: 'RP-N-002', state: '已发布', steps: [], scope: 'NonStandardWithDrawing', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: true, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-09-20T08:00:00Z',
      previewState: 'Failed', previewError: '发布包尚未进入服务器转换状态。', previewAttempts: 5,
    }
    const wrapper = mount(ReleaseOverview, {
      props: { releasePackages: [published], versions: [], baselines: [], pending: false },
    })

    const preview = wrapper.get('.pdm-release-stream-preview')
    expect(preview.text()).toContain('图纸转换（STEP/PDF）：转换失败（已重试 5 次）')
    expect(preview.text()).toContain('发布包尚未进入服务器转换状态。')
    expect(wrapper.get('.pdm-release-row-tail em').text()).toBe('转图失败')
    // 没有独立变更单号时状态行不能再显示占位符“—”（长交期等发布都会踩到）。
    expect(wrapper.find('.pdm-release-row-tail small').exists()).toBe(false)
    expect(wrapper.get('.pdm-release-row-tail').text()).toBe('转图失败')

    await preview.get('button').trigger('click')
    expect(wrapper.emitted('retryPreview')).toEqual([['release-published']])

    await wrapper.setProps({ releasePackages: [{ ...published, previewState: 'Succeeded', previewError: undefined }] })
    expect(wrapper.get('.pdm-release-stream-preview').text()).toContain('图纸转换（STEP/PDF）：已完成')
    expect(wrapper.get('.pdm-release-stream-preview').find('button').exists()).toBe(false)
  })

  it('keeps the previous error hidden while the conversion is running again', () => {
    const running: ReleasePackageSummary = {
      id: 'release-running', number: 'RP-N-004', state: '已发布', steps: [], scope: 'NonStandardWithDrawing', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: true, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], createdAt: '2026-09-20T08:00:00Z',
      previewState: 'Running', previewError: '调用转图服务器超过60分钟，已终止本次发布转换。', previewAttempts: 1,
    }
    const wrapper = mount(ReleaseOverview, { props: { releasePackages: [running], versions: [], baselines: [] } })

    const preview = wrapper.get('.pdm-release-stream-preview')
    expect(preview.text()).toContain('图纸转换（STEP/PDF）：转换中')
    // 正在转换时不能把上一次的失败原因当成本次结果展示。
    expect(preview.text()).not.toContain('超过60分钟')
    expect(wrapper.get('.pdm-release-row-tail em').text()).toBe('转换中')
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
