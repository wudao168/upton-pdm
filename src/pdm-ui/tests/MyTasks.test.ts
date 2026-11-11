import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import MyTasks from '../src/components/MyTasks.vue'
import type { MyApprovalTask, UserNotification } from '../src/types'

describe('MyTasks', () => {
  it('刷新位于工具栏最左侧并保留刷新事件与禁用状态', async () => {
    const wrapper=mount(MyTasks,{props:{tasks:[],notifications:[],locks:[],materialCodeTasks:[],passwordResetTasks:[],pending:false,onRequestRelease:vi.fn(),onForceRelease:vi.fn(),onResetPassword:vi.fn()}})
    expect(wrapper.get('.pdm-task-toolbar').element.firstElementChild?.textContent?.trim()).toBe('刷新')
    await wrapper.get('.pdm-task-toolbar > button').trigger('click')
    expect(wrapper.emitted('refresh')).toHaveLength(1)
    await wrapper.setProps({pending:true})
    expect(wrapper.get('.pdm-task-toolbar > button').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('同一发布审批通知与待办合为一条，已读及全部已读后仍保留待审批，完成后可查看历史', async () => {
    const task: MyApprovalTask = { id: 'approval-1', projectId: 'project-1', projectCode: 'P700016-4', projectName: '1111',
      releasePackageId: 'release-1', releasePackageNumber: 'RP-P700016-4-001', stage: 'MainDesigner', packageState: 'Approval', createdAt: '2026-11-04T08:06:09Z' }
    const notification: UserNotification = { id: 'notice-1', recipient: 'engineer', category: 'ReleaseApprovalPending', title: 'BOM发布待审批',
      content: 'P700016-4 · RP-P700016-4-001 待您审批', projectId: task.projectId, releasePackageId: task.releasePackageId,
      sourceKey: 'approval-round-1', createdAt: '2026-11-04T08:06:28Z' }
    const wrapper = mount(MyTasks, { props: { tasks: [task], notifications: [notification], locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
      onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn() } })
    const tab = (name: string) => wrapper.findAll('[role="tab"]').find(button => button.text().startsWith(name))!
    expect(tab('全部待办').text()).toBe('全部待办（1）')
    expect(tab('审批任务').text()).toBe('审批任务（1）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.get('tbody tr').text()).toContain('待审批')
    expect(wrapper.get('tbody tr').text()).toContain('RP-P700016-4-001')
    await wrapper.get('tbody .pdm-text-action').trigger('click')
    expect(wrapper.emitted('openNotification')).toEqual([[notification]])
    await wrapper.setProps({ notifications: [{ ...notification, readAt: '2026-11-04T08:07:00Z' }] })
    expect(tab('审批任务').text()).toBe('审批任务（1）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    await wrapper.setProps({ tasks: [] })
    expect(tab('全部待办').text()).toBe('全部待办（0）')
    expect(wrapper.get('tbody tr').text()).toContain('已读')
    await wrapper.get('.pdm-history-toggle input').setValue(false)
    expect(wrapper.findAll('tbody tr')).toHaveLength(0)
    wrapper.unmount()
  })

  it('重复及重提提醒只与对应发布包合并，其他发布包和退回通知继续独立显示', async () => {
    const task: MyApprovalTask = { id: 'approval-1', projectId: 'project-1', projectCode: 'P001', projectName: '项目一',
      releasePackageId: 'release-1', releasePackageNumber: 'RP-001', stage: 'Approval', packageState: 'Approval', createdAt: '2026-11-03T08:00:00Z' }
    const notice: UserNotification = { id: 'old', recipient: 'engineer', category: 'ReleaseApprovalPending', title: 'BOM发布待审批', content: '待您审批',
      projectId: task.projectId, releasePackageId: task.releasePackageId, sourceKey: 'old', createdAt: '2026-11-04T08:00:00Z', readAt: '2026-11-04T08:01:00Z' }
    const retry = { ...notice, id: 'retry', sourceKey: 'retry', title: 'BOM发布已重新提交审批', createdAt: '2026-11-04T09:00:00Z', readAt: undefined }
    const wrapper = mount(MyTasks, { props: { tasks: [task, { ...task, id: 'approval-2', releasePackageId: 'release-2', releasePackageNumber: 'RP-002' }],
      notifications: [retry, { ...notice, id: 'duplicate', readAt: undefined }, notice,
        { ...notice, id: 'other', releasePackageId: 'release-3', title: '其他发布包', readAt: undefined },
        { ...notice, id: 'rejected', category: 'ReleaseApprovalRejected', title: '发布审批已退回', readAt: undefined }],
      locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false, onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn() } })
    expect(wrapper.get('[role="tab"]').text()).toBe('全部待办（4）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(4)
    const merged = wrapper.findAll('tbody tr').find(row => row.text().includes('RP-001'))!
    expect(merged.text()).toContain('待审批')
    await merged.trigger('click')
    expect(wrapper.emitted('openNotification')).toEqual([[retry]])
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(4)
    expect(wrapper.get('tbody').text()).toContain('其他发布包')
    expect(wrapper.get('tbody').text()).toContain('发布审批已退回')
    wrapper.unmount()
  })

  it('发送回执保留在历史中并显示已发送，自己被指派的事项继续计入待办', async () => {
    const wrapper = mount(MyTasks, { props: { tasks: [], notifications: [
      { id: 'sent', recipient: 'sender', category: 'ProjectTodo', title: '日历待办 · sender', content: '已发送他人的任务', sourceKey: 'sent', createdAt: '2026-11-01T01:00:00Z', readAt: '2026-11-01T01:00:00Z', isSenderReceipt: true },
      { id: 'own', recipient: 'sender', category: 'ProjectTodo', title: '自己也要办理', content: '本人任务', sourceKey: 'own', createdAt: '2026-11-01T01:00:00Z' },
    ], locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false, onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn() } })
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).not.toContain('已发送他人的任务')
    expect(wrapper.get('[role="tab"]').text()).toBe('全部待办（1）')
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    expect(wrapper.text()).toContain('已发送')
    wrapper.unmount()
  })
  it('解除的生产资料风险移出未读待办，并在历史中显示已解除', async () => {
    const wrapper = mount(MyTasks, { props: { tasks: [], notifications: [
      { id: 'risk', recipient: 'manager', category: 'ReleaseManagementRisk', title: '生产资料未齐，需协调', content: 'P700016 · RP-01', sourceKey: 'risk', createdAt: '2026-11-01T01:00:00Z' },
      { id: 'closed', recipient: 'manager', category: 'ReleaseManagementRisk', title: '生产资料未齐，需协调', content: 'P700016 · RP-02', sourceKey: 'closed', createdAt: '2026-11-01T00:00:00Z', readAt: '2026-11-01T01:00:00Z', resolvedAt: '2026-11-01T01:00:00Z' },
      { id: 'old', recipient: 'manager', category: 'ReleasePreviewResult', title: '图纸转换已完成', content: '历史记录', sourceKey: 'old', createdAt: '2026-10-31T00:00:00Z', readAt: '2026-11-01T01:00:00Z' },
    ], locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false, onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn() } })
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.get('[role="tab"]').text()).toBe('全部待办（1）')
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    expect(wrapper.text()).toContain('已解除')
    expect(wrapper.text()).toContain('图纸转换已完成')
    wrapper.unmount()
  })
  it('工时催填的已读与未读记录均不进入列表、计数和分页，其余业务消息保留', async () => {
    const notifications = ['work-hours', 'work-hours', 'ReleaseApprovalRejected', 'ProjectTodo', 'work-hours-correction'].map((category, index) => ({
      id: `filter-${index}`, recipient: 'engineer', category, title: category === 'work-hours' ? '隐藏的工时催填' : `保留消息${index}`, content: '工时相关业务资料',
      sourceKey: `filter-${index}`, createdAt: '2026-10-31T02:00:00Z', ...(index === 0 || index === 4 ? { readAt: '2026-10-31T03:00:00Z' } : {}),
    }))
    const wrapper = mount(MyTasks, { props: { tasks: [], notifications, locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
      onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn() } })
    expect(wrapper.text()).not.toContain('隐藏的工时催填')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    expect(wrapper.get('[role="tab"]').text()).toBe('全部待办（2）')
    expect(wrapper.get('.pdm-task-pagination').text()).toContain('共 3 条')
    expect(wrapper.props('notifications')).toHaveLength(5)
    await wrapper.findAll('[role="tab"]').find(button => button.text().startsWith('系统消息'))!.trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('保留消息4')
    wrapper.unmount()
  })

  it('只有工时催填时显示零待办，不显示全部已读操作', () => {
    const wrapper = mount(MyTasks, { props: { tasks: [], notifications: [{ id: 'hours-only', recipient: 'engineer', category: 'work-hours', title: '请核对并补全工时', content: '历史欠填', sourceKey: 'hours-only', createdAt: '2026-10-31T02:00:00Z' }],
      locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false, onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn() } })
    expect(wrapper.get('[role="tab"]').text()).toBe('全部待办（0）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(0)
    expect(wrapper.text()).toContain('当前没有待办或消息')
    expect(wrapper.findAll('button').some(button => button.text() === '全部已读')).toBe(false)
    wrapper.unmount()
  })

  it('图纸与审批通知进入审批任务，转图及普通业务消息仍在系统消息，计数和入口同步', async () => {
    const categories = ['DrawingReviewAssigned', 'DrawingReviewSupervisorApproval', 'DrawingReviewChangesRequested',
      'DrawingReviewWritebackTimedOut', 'ReleaseApprovalPending', 'ReleaseApprovalRejected', 'WorkflowValidationPlan',
      'WorkflowMaterialCode', 'MaterialMasterRejected', 'MaterialCodeApplicationRejected', 'project-plan-approval', 'project-application',
      'ReleasePreviewPending', 'ReleasePreviewResult', 'work-hours', 'project-plan']
    const notifications = categories.map((category, index) => ({
      id: category, recipient: 'engineer', category, title: category, content: category + '内容', projectId: 'project',
      sourceKey: category, createdAt: '2026-10-28T02:00:00Z', ...(index === 2 ? { readAt: '2026-10-28T03:00:00Z' } : {}),
    }))
    const wrapper = mount(MyTasks, { props: {
      tasks: [{ id: 'task', projectId: 'project', projectCode: 'P001', projectName: '测试项目', releasePackageId: 'release',
        releasePackageNumber: 'RP-001', stage: 'Approval', packageState: 'Approval', createdAt: '2026-10-28T02:00:00Z' }],
      notifications, locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
      onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
    } })
    const approval = wrapper.findAll('[role="tab"]').find(button => button.text().includes('审批任务'))!
    const system = wrapper.findAll('[role="tab"]').find(button => button.text().includes('系统消息'))!
    expect(approval.text()).toBe('审批任务（12）')
    expect(system.text()).toBe('系统消息（3）')
    await approval.trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(12)
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(13)
    expect(wrapper.get('tbody').text()).toContain('DrawingReviewAssigned')
    expect(wrapper.get('tbody').text()).not.toContain('ReleasePreviewResult')
    expect(wrapper.get('tbody').text()).toContain('已读')
    const row = wrapper.findAll('tbody tr').find(item => item.text().includes('DrawingReviewAssigned'))!
    await row.findAll('button').find(button => button.text() === '查看审核')!.trigger('click')
    expect(wrapper.emitted('openNotification')).toEqual([[notifications[0]]])
    await system.trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    expect(wrapper.get('tbody').text()).toContain('ReleasePreviewResult')
    expect(wrapper.get('tbody').text()).not.toContain('DrawingReviewAssigned')
    expect(wrapper.get('tbody').text()).not.toContain('work-hours')
    wrapper.unmount()
  })

  it('页签只统计未读消息和未处理任务，已读与任务完成后动态归零并保留历史', async () => {
    const notifications = ['ReleaseApprovalRejected', 'project-plan', 'ProjectTodo'].map(category => ({
      id: category, recipient: 'engineer', category, title: category, content: '测试消息',
      sourceKey: category, createdAt: '2026-10-28T02:00:00Z',
    }))
    const wrapper = mount(MyTasks, { props: {
      tasks: [{ id: 'task', projectId: 'project', projectCode: 'P001', projectName: '测试项目', releasePackageId: 'release',
        releasePackageNumber: 'RP-001', stage: 'Approval', packageState: 'Approval', createdAt: '2026-10-28T02:00:00Z' }],
      notifications, locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
      onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
    } })
    const tab = (name: string) => wrapper.findAll('[role="tab"]').find(button => button.text().startsWith(name))!
    expect(tab('全部待办').text()).toBe('全部待办（4）')
    expect(tab('审批任务').text()).toBe('审批任务（2）')

    await wrapper.setProps({ notifications: notifications.map(item => ({ ...item, readAt: '2026-10-28T03:00:00Z' })) })
    expect(tab('全部待办').text()).toBe('全部待办（1）')
    expect(tab('审批任务').text()).toBe('审批任务（1）')
    expect(tab('系统消息').text()).toBe('系统消息（0）')
    expect(tab('项目待办').text()).toBe('项目待办（0）')

    await wrapper.setProps({ tasks: [] })
    expect(tab('全部待办').text()).toBe('全部待办（0）')
    expect(tab('审批任务').text()).toBe('审批任务（0）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(0)
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(3)
    await tab('系统消息').trigger('click')
    expect(wrapper.get('tbody').text()).toContain('已读')
    expect(wrapper.get('tbody').text()).toContain('project-plan')

    await wrapper.setProps({ notifications: [...(wrapper.props('notifications') ?? []), {
      ...notifications[1], id: 'new-message', title: '新消息',
    }] })
    expect(tab('全部待办').text()).toBe('全部待办（1）')
    expect(tab('系统消息').text()).toBe('系统消息（1）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    wrapper.unmount()
  })

  it('未读计数覆盖全部分页而非当前页，历史条数不变', async () => {
    const notifications = Array.from({ length: 38 }, (_, index) => ({
      id: `message-${index}`, recipient: 'engineer', category: 'project-plan', title: `消息${index}`, content: '测试消息',
      sourceKey: `message-${index}`, createdAt: '2026-10-28T02:00:00Z',
      ...(index < 37 ? { readAt: '2026-10-28T03:00:00Z' } : {}),
    }))
    const wrapper = mount(MyTasks, { props: {
      tasks: [], notifications, locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
      onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
    } })
    expect(wrapper.get('[role="tab"]').text()).toBe('全部待办（1）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    await wrapper.get('.pdm-history-toggle input').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(20)
    expect(wrapper.get('tbody').text()).not.toContain('未读')
    expect(wrapper.get('.pdm-task-pagination').text()).toContain('共 38 条')
    await wrapper.get('[aria-label="下一页"]').trigger('click')
    expect(wrapper.get('tbody').text()).toContain('未读')
    expect(wrapper.get('[role="tab"]').text()).toBe('全部待办（1）')
    wrapper.unmount()
  })

  it('shows persistent rejection notifications and deep-links to the release package', async () => {
    const onMarkAllNotificationsRead = vi.fn().mockResolvedValue(undefined)
    const notification = {
      id: 'notification-1', recipient: 'designer', category: 'ReleaseApprovalRejected',
      title: 'BOM发布审批已退回', content: 'P700005-3 · RP-001 被退回：结构需修改',
      projectId: 'project-1', releasePackageId: 'release-1', sourceKey: 'release-package:release-1:rejected:task-1',
      createdAt: '2026-09-03T08:11:56Z',
    }
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [], notifications: [notification], locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
        onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(), onMarkAllNotificationsRead,
      },
    })

    expect(wrapper.text()).toContain('BOM发布审批已退回')
    expect(wrapper.text()).toContain('结构需修改')
    await wrapper.findAll('button').find(button => button.text() === '查看发布包')!.trigger('click')
    expect(wrapper.emitted('openNotification')).toEqual([[notification]])
    await wrapper.findAll('button').find(button => button.text() === '全部已读')!.trigger('click')
    expect(onMarkAllNotificationsRead).toHaveBeenCalledOnce()
  })

  it('shows a material rejection message with a material action', async () => {
    const notification = {
      id: 'notification-material', recipient: 'engineer', category: 'MaterialMasterRejected',
      title: '料品申请已退回', content: '01021000055 · RFID读写头 被 standardizer 退回：型号资料不完整',
      sourceKey: 'material-master:material-1:rejected:v7', createdAt: '2026-09-17T07:00:00Z',
    }
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [], notifications: [notification], locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
        onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
      },
    })

    expect(wrapper.text()).toContain('料品申请已退回')
    expect(wrapper.text()).toContain('型号资料不完整')
    await wrapper.findAll('button').find(button => button.text() === '查看料品')!.trigger('click')
    expect(wrapper.emitted('openNotification')).toEqual([[notification]])
  })

  it('shows a project todo separately with its deadline and project link', async () => {
    const notification = {
      id: 'todo-1', recipient: 'engineer', category: 'ProjectTodo', title: 'P700001 待办事项', content: '确认客户现场准备情况',
      projectId: 'project-1', sourceKey: 'project-todo:project-1:todo-1', createdAt: '2026-09-25T08:00:00Z', dueDate: '2026-10-01',
    }
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [], notifications: [notification], locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
        onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
      },
    })

    expect(wrapper.text()).toContain('项目待办（1）')
    expect(wrapper.text()).toContain('截止 2026-10-01 · 确认客户现场准备情况')
    await wrapper.findAll('button').find(button => button.text() === '查看项目')!.trigger('click')
    expect(wrapper.emitted('openNotification')).toEqual([[notification]])
  })

  it('deep-links an approval task to its exact release package', async () => {
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [{
          id: 'task-1', projectId: 'project-1', projectCode: 'P700001', projectName: '气密设备',
          releasePackageId: 'release-1', releasePackageNumber: 'RP-S-001', stage: 'ProcessReview', packageState: 'Approval', createdAt: '2026-08-21T08:00:00Z',
        }],
        locks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
        onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
      },
    })

    const open = wrapper.findAll('button').find(button => button.text() === '查看')!
    await open.trigger('click')
    expect(wrapper.emitted('open')).toEqual([['project-1', 'release-1']])
  })

  it('uses one CRM-style message list with task type filters', async () => {
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [{
          id: 'task-1', projectId: 'project-1', projectCode: 'P700001', projectName: '气密设备',
          releasePackageId: 'release-1', releasePackageNumber: 'RP-P700001-0', stage: 'MainDesigner', packageState: 'Approval', createdAt: '2026-08-21T08:00:00Z',
        }],
        locks: [{
          documentId: 'document-1', projectId: 'project-1', projectCode: 'P700001', projectName: '气密设备', drawingNumber: 'LOCK-001', documentName: '机架', fileName: '机架.SLDASM',
          checkedOutBy: 'engineer', checkedOutAt: '2026-08-20T08:00:00Z', checkoutMachine: 'DESIGN-WS', lastHeartbeatAt: '2026-08-21T08:00:00Z', leaseExpiresAt: '2026-08-21T09:00:00Z',
          connectionState: 'Offline', attentionLevel: 'Reclaimable', ownedByCurrentUser: false, canRequestRelease: false, canForceRelease: true,
        }],
        materialCodeTasks: [{
          id: 'material-1', projectId: 'project-1', projectCode: 'P700001', projectName: '气密设备',
          bomItemId: null, bomHeaderKind: 'Standard', applicationType: 'BomHeader', status: 'Pending',
          workflowState: 'PendingApproval', requestedBy: 'engineer', requestedAt: '2026-08-21T09:00:00Z', rowVersion: 1,
        }],
        passwordResetTasks: [{ id: 'password-1', username: 'zhangsan', displayName: '张三', requestedAt: '2026-08-19T08:00:00Z' }],
        pending: false, onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
      },
    })

    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('[aria-label="待办类型"]').text()).toContain('全部待办（4）')
    expect(wrapper.text()).toContain('P700001 BOM发布审批')
    expect(wrapper.text()).toContain('P700001 BOM料号审批')
    expect(wrapper.text()).toContain('申请人 engineer')
    expect(wrapper.text()).toContain('P700001 · 气密设备')
    expect(wrapper.text()).toContain('zhangsan 密码重置申请')

    await wrapper.get('[role="tab"][aria-selected="false"]:nth-of-type(4)').trigger('click')
    expect(wrapper.text()).toContain('P700001 · 气密设备')
    expect(wrapper.text()).not.toContain('P700001 BOM发布审批')
    expect(wrapper.text()).not.toContain('zhangsan 密码重置申请')
  })

  it('groups BOM number approvals from the same project into one task row', async () => {
    const baseTask = {
      projectId: 'project-2', projectCode: 'P700002', projectName: 'XXX设备',
      bomItemId: null, applicationType: 'BomHeader' as const, status: 'Pending' as const,
      workflowState: 'PendingApproval' as const, requestedBy: 'developer', rowVersion: 1,
    }
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [], locks: [], passwordResetTasks: [], pending: false,
        materialCodeTasks: [
          { ...baseTask, id: 'master', bomHeaderKind: 'Master' as const, requestedAt: '2026-08-25T02:19:09Z' },
          { ...baseTask, id: 'standard', bomHeaderKind: 'Standard' as const, requestedAt: '2026-08-25T02:19:10Z' },
          { ...baseTask, id: 'non-standard', bomHeaderKind: 'NonStandard' as const, requestedAt: '2026-08-25T02:19:10Z' },
          { ...baseTask, id: 'electrical', bomHeaderKind: 'Electrical' as const, requestedAt: '2026-08-25T02:19:18Z' },
        ],
        onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
      },
    })

    expect(wrapper.get('[role="tab"]:nth-of-type(3)').text()).toBe('料号审批（1）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('P700002 BOM料号审批（4项）')
    expect(wrapper.text()).toContain('项目主BOM、标准件BOM、非标件BOM、电气件BOM')
    expect(wrapper.text()).toContain('共4项')

    await wrapper.get('.pdm-text-action').trigger('click')
    expect(wrapper.emitted('openMaterialApprovals')).toHaveLength(1)
  })

  it('requests editing in one click even when another person has already requested it', async () => {
    const onRequestRelease = vi.fn().mockResolvedValue(undefined)
    const wrapper = mount(MyTasks, { props: {
      tasks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
      locks: [{ documentId: 'document-1', projectId: 'project-1', projectCode: 'P700001', projectName: '测试项目', drawingNumber: 'P-1', documentName: '机架', fileName: 'P-1.SLDPRT',
        checkedOutBy: 'engineer', checkedOutAt: '2026-10-13T00:00:00Z', lastHeartbeatAt: '2026-10-13T00:00:00Z', leaseExpiresAt: '2026-10-13T00:15:00Z', connectionState: 'Active', attentionLevel: 'Normal',
        ownedByCurrentUser: false, canRequestRelease: true, canForceRelease: false, releaseRequestedBy: 'someone-else' }],
      onRequestRelease, onForceRelease: vi.fn(), onResetPassword: vi.fn(),
    } })
    await wrapper.findAll('button').find(button => button.text() === '我需要编辑')!.trigger('click')
    await flushPromises()
    expect(onRequestRelease).toHaveBeenCalledExactlyOnceWith('document-1', '需要继续编辑该图档，请在完成后关闭图档。')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('groups edit permissions by project and releases the project in one action', async () => {
    const onForceRelease = vi.fn().mockResolvedValue(undefined)
    const lock = {
      projectId: 'project-1', projectCode: 'P700001-1', projectName: '机架',
      checkedOutBy: 'engineer', checkedOutAt: '2026-08-20T08:00:00Z', checkoutMachine: 'DESIGN-WS', lastHeartbeatAt: '2026-08-21T08:00:00Z', leaseExpiresAt: '2026-08-21T09:00:00Z',
      connectionState: 'Offline' as const, attentionLevel: 'Reclaimable' as const, ownedByCurrentUser: false, canRequestRelease: false, canForceRelease: true,
    }
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [], materialCodeTasks: [], passwordResetTasks: [], pending: false,
        locks: [
          { ...lock, documentId: 'document-1', drawingNumber: 'LOCK-001', documentName: '标板切板盒', fileName: 'LOCK-001.SLDPRT' },
          { ...lock, documentId: 'document-2', drawingNumber: 'LOCK-002', documentName: '顶板薄', fileName: 'LOCK-002.SLDPRT' },
          { ...lock, projectId: 'project-2', projectCode: 'P700002', projectName: '11', documentId: 'document-3', drawingNumber: 'LOCK-003', documentName: '11工程图', fileName: 'LOCK-003.SLDDRW' },
        ],
        onRequestRelease: vi.fn(), onForceRelease, onResetPassword: vi.fn(),
      },
      global: {
        stubs: {
          ElDialog: { props: ['modelValue', 'title'], template: '<div v-if="modelValue" role="dialog"><h2>{{ title }}</h2><slot/><slot name="footer"/></div>' },
        },
      },
    })

    expect(wrapper.get('[role="tab"]:nth-of-type(4)').text()).toBe('编辑权限（2）')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    expect(wrapper.text()).toContain('P700001-1 · 机架')
    expect(wrapper.text()).toContain('2 个图档')

    await wrapper.findAll('button').find(button => button.text() === '统一强制释放（2）')!.trigger('click')
    expect(wrapper.get('[role="dialog"]').text()).toContain('常用释放原因')
    expect(wrapper.get('[role="dialog"]').text()).toContain('项目交接需继续设计')
    await wrapper.findAll('button').find(button => button.text() === '项目交接需继续设计')!.trigger('click')
    expect((wrapper.get('[aria-label="强制释放原因"]').element as HTMLTextAreaElement).value).toBe('项目交接需继续设计')
    await wrapper.get('[aria-label="强制释放原因"]').setValue('项目统一释放')
    await wrapper.findAll('button').find(button => button.text() === '确认释放')!.trigger('click')
    await flushPromises()

    expect(onForceRelease.mock.calls).toEqual([
      ['document-1', '项目统一释放'],
      ['document-2', '项目统一释放'],
    ])
  })
})
