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
})
