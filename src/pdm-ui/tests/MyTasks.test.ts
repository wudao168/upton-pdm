import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import MyTasks from '../src/components/MyTasks.vue'

describe('MyTasks', () => {
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
    expect(wrapper.get('[aria-label="消息中心"]').text()).toContain('全部待办（4）')
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
    expect(wrapper.text()).toContain('项目主BOM、标准件BOM、非标件BOM、电气BOM')
    expect(wrapper.text()).toContain('共4项')

    await wrapper.get('.pdm-text-action').trigger('click')
    expect(wrapper.emitted('openMaterialApprovals')).toHaveLength(1)
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
