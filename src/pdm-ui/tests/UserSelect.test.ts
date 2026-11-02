import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, ref } from 'vue'
import ElementPlus from 'element-plus'
import UserSelect from '../src/components/UserSelect'

let wrapper: ReturnType<typeof mount>
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })
const people = [
  { username: 'engineer.01', displayName: '张明' },
  { username: 'engineer.02', displayName: '王一' },
]
function mountSelect(native = false, multiple = false) {
  wrapper = mount(defineComponent({
    components: { UserSelect },
    setup() { return { people, chosen: ref(multiple ? ['engineer.02'] : '') } },
    template: native
      ? '<UserSelect v-model="chosen" aria-label="人员"><option value="">全部人员</option><option v-for="person in people" :key="person.username" :value="person.username">{{ person.displayName }}</option></UserSelect>'
      : `<UserSelect v-model="chosen" aria-label="人员" ${multiple ? 'multiple' : ''}><el-option v-for="person in people" :key="person.username" :value="person.username" :label="person.displayName" /></UserSelect>`,
  }), { attachTo: document.body, global: { plugins: [ElementPlus] } })
  return wrapper.get('input[role="combobox"]')
}
const options = () => [...document.querySelectorAll('[role="option"]')].filter(node => (node as HTMLElement).style.display !== 'none')

describe('共用人员选择', () => {
  it.each([false, true])('filters Chinese names by initials and returns the original account (native options: %s)', async native => {
    const input = mountSelect(native)
    if (native) expect(wrapper.get('.el-select__placeholder').text()).toBe('全部人员')
    await wrapper.get('.el-select__wrapper').trigger('click')
    await input.setValue('ZM')
    await flushPromises()
    expect(options().map(node => node.textContent?.trim())).toEqual(['张明'])
    ;(options()[0] as HTMLElement).click()
    await flushPromises()
    expect((wrapper.vm as unknown as { chosen: string }).chosen).toBe('engineer.01')
    expect(wrapper.get('.el-select__placeholder').text()).toBe('张明')
    await wrapper.get('.el-select__wrapper').trigger('click')
    await input.setValue('ENGINEER.02')
    await flushPromises()
    expect(options().map(node => node.textContent?.trim())).toEqual(['王一'])
  })

  it('retains selected people during filtering and resets the query after closing', async () => {
    const input = mountSelect(false, true)
    await wrapper.get('.el-select__wrapper').trigger('click')
    await input.setValue('zm')
    await flushPromises()
    expect(wrapper.get('.el-tag').text()).toContain('王一')
    await input.trigger('keydown', { code: 'Escape' })
    await flushPromises()
    await wrapper.get('.el-select__wrapper').trigger('click')
    await input.setValue('')
    await flushPromises()
    expect(options().map(node => node.textContent?.trim())).toEqual(['张明', '王一'])
    expect((wrapper.vm as unknown as { chosen: string[] }).chosen).toEqual(['engineer.02'])
  })
})
