import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ProjectWorkspaceHeader from '../src/components/ProjectWorkspaceHeader.vue'
import type { ProjectSummary } from '../src/types'

const rootProject: ProjectSummary = {
  id: 'root-1',
  code: 'P700002',
  name: 'XXX设备',
  owner: 'engineer',
  stage: '进行中',
  vaultName: 'P700002',
  vaultLocation: 'D:\\PDM\\P700002',
  releaseLocation: 'D:\\Release\\P700002',
  customerName: '中山联合光电科技有限公司',
  executionUnitName: '自动化事业部',
  primaryProjectManager: 'project-manager',
  designLead: 'design-lead',
  deviceModel: 'AK-1-C00003-001-00',
  quantity: 1,
  serialNumbers: ['70000001'],
  responsibleUsers: ['engineer'],
  collaborativeProjectManagers: [],
  designers: [],
  documentCount: 3,
  businessStatus: '已检出',
  canAssignExecutionUnit: false,
  canManageMainStaffing: false,
  canAssignDesigners: false,
  canReadContent: true,
}

const childProject: ProjectSummary = {
  ...rootProject,
  id: 'child-1',
  code: 'P700002-1',
  name: '机架',
  parentProjectId: rootProject.id,
  childSequence: 1,
  deviceModel: 'AK-1-C00003-001-01',
  serialNumbers: ['70000002'],
  documentCount: 0,
  businessStatus: '待审批',
}

const drawingChild: ProjectSummary = {
  ...childProject,
  id: 'child-2',
  code: 'P700002-2',
  name: '工装',
  childSequence: 2,
  documentCount: 2,
  businessStatus: '正常',
}

describe('ProjectWorkspaceHeader', () => {
  it('左侧固定显示主项目与全部项目号，右侧显示选中项目型号和序列号', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild], activeTab: 'overview' },
    })

    expect(wrapper.get('[aria-label="项目基本信息与全部项目号"]').text()).toContain('中山联合光电科技有限公司')
    expect(wrapper.get('[aria-label="项目基本信息与全部项目号"]').text()).not.toContain('项目基本信息')
    expect(wrapper.findAll('.pdm-project-family__list button')).toHaveLength(3)
    expect(wrapper.get('[aria-label="选择项目号 P700002"]').classes()).toContain('has-drawings')
    expect(wrapper.get('[aria-label="选择项目号 P700002-1"]').classes()).toContain('is-active')
    expect(wrapper.get('.pdm-project-selected-summary').text()).toContain('AK-1-C00003-001-01')
    expect(wrapper.get('.pdm-project-selected-summary').text()).toContain('70000002')
  })

  it('点击项目号切换右侧数据范围，功能选项卡保持独立', async () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild], activeTab: 'overview' },
    })

    await wrapper.get('[aria-label="选择项目号 P700002-2"]').trigger('click')
    expect(wrapper.emitted('switch')).toEqual([[drawingChild.id]])

    const fileTab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text() === '文件库')
    expect(fileTab).toBeDefined()
    await fileTab!.trigger('click')
    expect(wrapper.emitted('tab')).toEqual([['files']])
  })

  it('图档页将项目图档概览固定在左侧项目号下方', () => {
    const wrapper = mount(ProjectWorkspaceHeader, {
      props: { project: childProject, projects: [rootProject, childProject, drawingChild], activeTab: 'documents' },
      slots: { sidebar: '<section aria-label="BOM完整性">BOM完整性</section><section aria-label="当前发布包">当前发布包</section>' },
    })

    const sidebar = wrapper.get('[aria-label="项目基本信息与全部项目号"]')
    expect(sidebar.get('[aria-label="项目图档概览"]').text()).toContain('BOM完整性')
    expect(sidebar.get('[aria-label="项目图档概览"]').text()).toContain('当前发布包')
    expect(wrapper.get('.pdm-project-family').element.nextElementSibling).toBe(sidebar.get('[aria-label="项目图档概览"]').element)
  })
})
