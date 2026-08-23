import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import MyTasks from '../src/components/MyTasks.vue'

describe('MyTasks', () => {
  it('deep-links an approval task to its exact release package', async () => {
    const wrapper = mount(MyTasks, {
      props: {
        tasks: [{
          id: 'task-1', projectId: 'project-1', projectCode: 'P700001', projectName: '气密设备',
          releasePackageId: 'release-1', releasePackageNumber: 'RP-S-001', stage: 'ProcessReview', packageState: 'Approval', createdAt: '2026-08-21T08:00:00Z',
        }],
        locks: [], passwordResetTasks: [], pending: false,
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
        passwordResetTasks: [{ id: 'password-1', username: 'zhangsan', displayName: '张三', requestedAt: '2026-08-19T08:00:00Z' }],
        pending: false, onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(),
      },
    })

    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('[aria-label="消息中心"]').text()).toContain('全部待办（3）')
    expect(wrapper.text()).toContain('P700001 BOM发布审批')
    expect(wrapper.text()).toContain('P700001 · LOCK-001')
    expect(wrapper.text()).toContain('zhangsan 密码重置申请')

    await wrapper.get('[role="tab"][aria-selected="false"]:nth-of-type(3)').trigger('click')
    expect(wrapper.text()).toContain('P700001 · LOCK-001')
    expect(wrapper.text()).not.toContain('P700001 BOM发布审批')
    expect(wrapper.text()).not.toContain('zhangsan 密码重置申请')
  })
})
