import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import AuditLog from '../src/components/AuditLog.vue'

const apiMocks = vi.hoisted(() => ({ addProjectManagerNote: vi.fn() }))

vi.mock('../src/api', () => apiMocks)
vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), error: vi.fn(), warning: vi.fn() } }))

describe('AuditLog', () => {
  it('项目经理在记录页维护备注，其他用户仅查看日志', async () => {
    apiMocks.addProjectManagerNote.mockResolvedValue({})
    const wrapper = mount(AuditLog, { props: { entries: [], projectId: 'project-1', token: 'token', canAddManagerNote: true } })

    await wrapper.get('textarea').setValue('采购风险已同步')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(apiMocks.addProjectManagerNote).toHaveBeenCalledWith('project-1', '采购风险已同步', 'token')
    expect(wrapper.emitted('refresh')).toEqual([[]])

    await wrapper.setProps({ canAddManagerNote: false })
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('项目记录只显示备注、待办推送、BOM发布和已完成任务', () => {
    const wrapper = mount(AuditLog, { props: {
      projectRecordsOnly: true,
      projects: [
        { id: 'project-1', code: 'P700001', name: '主项目' },
        { id: 'project-2', code: 'P700001-1', name: '子项目' },
      ],
      entries: [
        { id: 'note', projectId: 'project-1', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'project-1', detail: '客户已确认方案', occurredAt: '2026-09-25T08:30:00Z' },
        { id: 'todo', projectId: 'project-1', actor: 'manager', action: 'project.todo.create', entityType: 'Project', entityId: 'project-1', detail: '创建待办并推送给 工程师甲、采购乙；截止 2026-09-30：确认物料到货计划', occurredAt: '2026-09-25T08:30:30Z' },
        { id: 'published', projectId: 'project-2', actor: 'manager', action: 'release-package.publish', entityType: 'ReleasePackage', entityId: 'release-1', detail: 'BOM正式发布', occurredAt: '2026-09-25T08:31:00Z' },
        { id: 'completed', projectId: 'project-2', actor: 'engineer', action: 'project-plan.progress', entityType: 'ProjectPlanTask', entityId: 'task-1', detail: 'P700001；图纸审核；100%', occurredAt: '2026-09-25T08:32:00Z' },
        { id: 'excluded', projectId: 'project-2', actor: 'engineer', action: 'bom.replace', entityType: 'BomItem', entityId: 'bom-1', detail: 'Standard:7', occurredAt: '2026-09-25T08:33:00Z' },
      ],
    } })

    expect(wrapper.text()).toContain('项目经理备注')
    expect(wrapper.text()).toContain('待办推送')
    expect(wrapper.text()).toContain('待办人：工程师甲、采购乙')
    expect(wrapper.text()).toContain('截止日期：2026-09-30')
    expect(wrapper.text()).toContain('确认物料到货计划')
    expect(wrapper.text()).toContain('BOM发布')
    expect(wrapper.text()).toContain('任务完成')
    expect(wrapper.text()).toContain('P700001-1')
    expect(wrapper.text()).not.toContain('Standard:7')
    expect(wrapper.findAll('tbody tr')).toHaveLength(4)
  })

  it('可按关键词筛选主项目及子项目的统一记录', async () => {
    const wrapper = mount(AuditLog, { props: { projectRecordsOnly: true, projects: [
      { id: 'root', code: 'P700001', name: '主项目' },
      { id: 'child', code: 'P700001-1', name: '子项目' },
    ], entries: [
      { id: 'root-note', projectId: 'root', actor: 'manager', action: 'project.manager-note', entityType: 'Project', entityId: 'root', detail: '主项目风险', occurredAt: '2026-09-25T08:30:00Z' },
      { id: 'child-note', projectId: 'child', actor: 'engineer', action: 'project.manager-note', entityType: 'Project', entityId: 'child', detail: '子项目已完成', occurredAt: '2026-09-25T08:31:00Z' },
    ] } })

    await wrapper.get('[aria-label="搜索项目记录"]').setValue('子项目')
    expect(wrapper.text()).toContain('子项目已完成')
    expect(wrapper.text()).not.toContain('主项目风险')
  })
})
