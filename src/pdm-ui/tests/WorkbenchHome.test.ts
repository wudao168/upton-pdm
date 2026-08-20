import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import WorkbenchHome from '../src/components/WorkbenchHome.vue'
import type { DocumentNode, ProjectSummary } from '../src/types'

const project = {
  code: 'P700001',
  name: '气密设备',
  vaultLocation: 'D:\\PDM\\Vault\\P700001',
  releaseLocation: 'D:\\PDM\\Release\\P700001',
} as ProjectSummary

const selected = {
  drawingNumber: '123',
  name: '123',
  fileName: '123.SLDASM',
  version: 'W18',
  configuration: '默认',
} as DocumentNode

describe('WorkbenchHome', () => {
  it('删除概览页头，并通过统计卡直接进入图档和BOM', async () => {
    const wrapper = mount(WorkbenchHome, {
      props: {
        project,
        selected,
        hasDocuments: true,
        documentCount: 48,
        warningCount: 0,
        standardCount: 2,
        nonStandardCount: 1,
        electricalCount: 0,
        releasePackage: null,
      },
    })

    expect(wrapper.find('.pdm-project-overview-heading').exists()).toBe(false)
    expect(wrapper.find('.pdm-page-actions').exists()).toBe(false)

    await wrapper.get('button[aria-label="进入项目图档"]').trigger('click')
    await wrapper.get('button[aria-label="进入BOM数据"]').trigger('click')

    expect(wrapper.emitted('documents')).toEqual([[]])
    expect(wrapper.emitted('bom')).toEqual([[]])
  })
})
