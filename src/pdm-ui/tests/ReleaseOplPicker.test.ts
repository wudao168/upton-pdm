import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ReleaseOplPicker from '../src/components/ReleaseOplPicker.vue'

const read = vi.hoisted(() => vi.fn())
vi.mock('../src/api', () => ({ readProjectOpl: read }))
const rows = [
  { id: 'a', number: 1, description: '相机支架松动', status: '待处理', owner: 'engineer', relatedProjectId: null },
  { id: 'b', number: 2, description: '电气接线变更', status: '已完成', owner: 'engineer', relatedProjectId: 'child' },
  { id: 'c', number: 3, description: '其他子项目', status: '处理中', relatedProjectId: 'sibling' },
  { id: 'd', number: 4, description: '作废问题', status: '已作废' },
].map(issue => ({ issue, canEdit: true, canComplete: true }))
const data = { rows, people: [{ username: 'engineer', displayName: '王工' }], rootProject: { id: 'root' }, subprojects: [{ id: 'child', code: 'P-1' }] }

describe('release OPL picker', () => {
  it('supports multiple issues, search and completed issues while restricting sibling project scope', async () => {
    read.mockResolvedValue(data)
    const wrapper = mount(ReleaseOplPicker, { props: { projectId: 'child', token: 'token', modelValue: [] } })
    await flushPromises()
    expect(wrapper.findAll('input[type=checkbox]')).toHaveLength(1)
    await wrapper.get('[aria-label="关联OPL 1"]').setValue(true)
    expect(wrapper.emitted('update:modelValue')?.[0]).toEqual([['a']])
    await wrapper.setProps({ modelValue: ['a'] })
    await wrapper.get('select').setValue('')
    await wrapper.get('[aria-label="关联OPL 2"]').setValue(true)
    expect(wrapper.emitted('update:modelValue')?.[1]).toEqual([['a', 'b']])
    expect(wrapper.text()).not.toContain('其他子项目')
    expect(wrapper.text()).not.toContain('作废问题')
    await wrapper.get('[aria-label="搜索关联OPL问题"]').setValue('接线')
    expect(wrapper.findAll('input[type=checkbox]')).toHaveLength(1)
    expect(wrapper.text()).toContain('王工')
    wrapper.unmount()
  })
  it('preserves saved selections on load failure and displays readonly historical associations', async () => {
    read.mockRejectedValueOnce(new Error('network failed'))
    const wrapper = mount(ReleaseOplPicker, { props: { projectId: 'root', token: 'token', modelValue: ['a'] } })
    await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('已选关联仍保留')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    read.mockResolvedValue(data)
    await wrapper.get('button').trigger('click'); await flushPromises()
    await wrapper.setProps({ readonly: true, modelValue: ['a', 'b', 'd'] })
    expect(wrapper.findAll('input[type=checkbox]')).toHaveLength(0)
    expect(wrapper.text()).toContain('相机支架松动')
    expect(wrapper.text()).toContain('电气接线变更')
    expect(wrapper.text()).toContain('作废问题')
    wrapper.unmount()
  })
})
