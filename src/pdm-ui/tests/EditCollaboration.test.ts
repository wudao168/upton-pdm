import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import MyTasks from '../src/components/MyTasks.vue'
import type { EditLockSummary } from '../src/types'

const lock: EditLockSummary = {
  documentId: 'doc', projectId: 'project', projectCode: 'P700001', projectName: '协作项目', drawingNumber: 'PART', documentName: '零件', fileName: 'PART.SLDPRT',
  checkedOutBy: 'engineer', checkedOutAt: '2026-10-23T01:00:00Z', lastHeartbeatAt: '2026-10-23T01:30:00Z', leaseExpiresAt: '2026-10-23T01:35:00Z',
  connectionState: 'Active', attentionLevel: 'Normal', ownedByCurrentUser: false, canRequestRelease: true, canForceRelease: false,
  canEmergencyRelease: true, sessionId: 'original-session',
}
function mountTasks(locks: EditLockSummary[], onEmergencyRelease = vi.fn().mockResolvedValue(undefined)) {
  return { onEmergencyRelease, wrapper: mount(MyTasks, {
    props: { tasks: [], locks, materialCodeTasks: [], passwordResetTasks: [], pending: false, onRequestRelease: vi.fn(), onForceRelease: vi.fn(), onResetPassword: vi.fn(), onEmergencyRelease },
    global: { stubs: { ElDialog: { props: ['modelValue', 'title'], template: '<div v-if="modelValue" role="dialog"><h2>{{ title }}</h2><slot/><slot name="footer"/></div>' } } },
  }) }
}
describe('图档协作接管', () => {
  it('有授权时可接管在线会话，提交具体会话和获取时间并要求原因', async () => {
    const { wrapper, onEmergencyRelease } = mountTasks([lock])
    await wrapper.findAll('button').find(button => button.text() === '紧急接管')!.trigger('click')
    expect(wrapper.get('[role=dialog]').text()).toContain('未上传修改需要在原电脑另存')
    await wrapper.get('[role=dialog]').findAll('button').find(button => button.text() === '确认释放')!.trigger('click')
    expect(onEmergencyRelease).not.toHaveBeenCalled()
    await wrapper.get('textarea').setValue('项目负责人确认交接')
    await wrapper.get('[role=dialog]').findAll('button').find(button => button.text() === '确认释放')!.trigger('click')
    await flushPromises()
    expect(onEmergencyRelease).toHaveBeenCalledExactlyOnceWith('doc', 'original-session', lock.checkedOutAt, '项目负责人确认交接')
    wrapper.unmount()
  })
  it('没有紧急接管授权或缺少会话信息时不显示该操作', () => {
    for (const value of [{ ...lock, canEmergencyRelease: false }, { ...lock, sessionId: undefined }]) {
      const { wrapper } = mountTasks([value])
      expect(wrapper.findAll('button').some(button => button.text() === '紧急接管')).toBe(false)
      wrapper.unmount()
    }
  })
})
