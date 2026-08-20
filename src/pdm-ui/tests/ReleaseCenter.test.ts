import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ReleaseCenter from '../src/components/ReleaseCenter.vue'

describe('ReleaseCenter', () => {
  it('creates one ECN that fixes serial effectivity and all three independent BOM versions', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        serialNumbers: ['70000001', '70000002'],
        username: 'admin',
        pending: false,
        progress: 0,
        error: '',
        canManage: true,
        canDecide: true,
      },
    })

    await wrapper.get('input[list="pdm-release-serials"]').setValue('70000002')
    await wrapper.get('textarea').setValue('标准件增加两个备件，非标件和电气BOM保持原版本')
    await wrapper.get('form').trigger('submit')

    const created = wrapper.emitted('create')?.[0]
    expect(created?.[1]).toMatch(/^ECN-/)
    expect(created?.[2]).toBe('标准件增加两个备件，非标件和电气BOM保持原版本')
    expect(created?.[3]).toBe('70000002')
    expect(created?.[4]).toBe('')
    expect(created?.[5]).toBe('admin')
    expect(created?.[6]).toBe('admin')
  })
})
