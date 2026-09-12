import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '../src/api'
import ProjectWorkbench from '../src/components/ProjectWorkbench.vue'
import type { ProjectPlan, ProjectPlanPortfolio, ProjectPlanTask, ProjectSummary } from '../src/types'

vi.mock('../src/api', () => ({ readProjectPlanPortfolio: vi.fn() }))

function project(id: string, code: string, name: string, manager: string, collaborativeProjectManagers: string[] = []): ProjectSummary {
  return {
    id, code, name, owner: manager, stage: '进行中', vaultName: 'PDM', vaultLocation: `D:\\PDM\\${code}`, releaseLocation: `D:\\Release\\${code}`,
    customerName: `${name}客户`, quantity: 1, serialNumbers: [], responsibleUsers: [manager], primaryProjectManager: manager,
    collaborativeProjectManagers, designers: [], canAssignExecutionUnit: false, canManageMainStaffing: false, canAssignDesigners: false, canReadContent: true,
  }
}

function task(id: string, name: string, start: string, finish: string, assignee = 'engineer', milestone = false): ProjectPlanTask {
  return {
    id, name, stage: 'Design', assignee, durationDays: Math.max(0, Math.round((Date.parse(`${finish}T00:00:00Z`) - Date.parse(`${start}T00:00:00Z`)) / 86_400_000) + 1),
    plannedStart: start, plannedFinish: finish, completionPercent: 0, status: 'NotStarted', predecessorTaskIds: [], weight: 1,
    isMilestone: milestone, isRequired: true, sortOrder: 1,
  }
}

function plan(projectId: string, tasks: ProjectPlanTask[], pendingChange = false): ProjectPlan {
  return {
    id: `plan-${projectId}`, projectId, templateId: 'template', templateName: '标准计划', currentStage: 'Design', plannedStart: tasks[0]!.plannedStart,
    plannedFinish: tasks.at(-1)!.plannedFinish, forecastFinish: tasks.at(-1)!.plannedFinish, baselineVersion: 1, tasks,
    stages: [{ code: 'Design', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 }], approvalStatus: 'Approved',
    changeRequest: pendingChange ? { id: 'change', tasks: [], reason: '调整', submittedBy: 'pm', submittedAt: '2026-09-11T00:00:00Z', approvalAssignee: 'boss', status: 'Pending' } : null,
    createdBy: 'pm', createdAt: '2026-09-01T00:00:00Z', updatedBy: 'pm', updatedAt: '2026-09-01T00:00:00Z', rowVersion: 1,
  }
}

function portfolio(root: ProjectSummary, projectPlan: ProjectPlan): ProjectPlanPortfolio {
  return {
    rootProjectId: root.id, currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0,
    plannedStart: projectPlan.plannedStart, plannedFinish: projectPlan.plannedFinish,
    projects: [{ projectId: root.id, projectCode: root.code, projectName: root.name, isRoot: true, hasPlan: true, currentStage: 'Design', completionPercent: 0, plannedStart: projectPlan.plannedStart, plannedFinish: projectPlan.plannedFinish, forecastFinish: projectPlan.forecastFinish, isLagging: false, isAtRisk: false, plan: projectPlan }],
  }
}

describe('ProjectWorkbench', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-12T08:00:00+08:00'))
    vi.mocked(api.readProjectPlanPortfolio).mockReset()
  })

  afterEach(() => vi.useRealTimers())

  it('汇总项目经理的并行项目、风险节点和跨项目资源冲突', async () => {
    const first = project('root-1', 'P700101', '装配线一', 'pm')
    const second = project('root-2', 'P700102', '装配线二', 'other', ['pm'])
    const hidden = project('root-3', 'P700103', '无关项目', 'other')
    const firstPlan = plan(first.id, [
      task('task-overdue', '设计评审', '2026-09-05', '2026-09-10'),
      task('task-milestone', '设计冻结', '2026-09-15', '2026-09-15', 'engineer', true),
    ])
    const secondPlan = plan(second.id, [task('task-overlap', '机械设计', '2026-09-09', '2026-09-20')], true)
    vi.mocked(api.readProjectPlanPortfolio).mockImplementation(async projectId => projectId === first.id ? portfolio(first, firstPlan) : portfolio(second, secondPlan))

    const wrapper = mount(ProjectWorkbench, {
      props: {
        projects: [first, second, hidden], users: [{ username: 'engineer', displayName: '马文豪', role: 'Engineer', isActive: true }],
        token: 'token', currentUsername: 'pm', canViewAll: false,
      },
    })
    await flushPromises()

    expect(api.readProjectPlanPortfolio).toHaveBeenCalledTimes(2)
    expect(wrapper.get('[aria-label="项目关键指标"]').text()).toContain('进行中项目2')
    expect(wrapper.get('[aria-label="项目关键指标"]').text()).toContain('逾期项目1')
    expect(wrapper.get('[aria-label="项目关键指标"]').text()).toContain('7天内节点1')
    expect(wrapper.get('[aria-label="项目关键指标"]').text()).toContain('待审批／变更1')
    expect(wrapper.get('[aria-label="项目关键指标"]').text()).toContain('资源冲突人员1')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('P700101')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('P700102')
    expect(wrapper.get('[aria-label="项目总览"]').text()).not.toContain('P700103')
    expect(wrapper.get('[aria-label="跨项目资源冲突"]').text()).toContain('马文豪')
    expect(wrapper.get('[aria-label="跨项目资源冲突"]').text()).toContain('2 个项目')
    expect(wrapper.get('[aria-label="跨项目阶段甘特图"]').text()).toContain('设计')

    const overdueCard = wrapper.findAll('.pdm-project-workbench__metric').find(card => card.text().includes('逾期项目'))!
    await overdueCard.trigger('click')
    expect(wrapper.get('[aria-label="项目总览"]').text()).toContain('P700101')
    expect(wrapper.get('[aria-label="项目总览"]').text()).not.toContain('P700102')

    await wrapper.get('button[aria-label="进入项目 P700101"]').trigger('click')
    expect(wrapper.emitted('open')?.at(-1)).toEqual(['root-1', 'overview'])
  })
})
