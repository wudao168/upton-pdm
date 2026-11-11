import { mount } from '@vue/test-utils'
import { expect, it } from 'vitest'
import PersonName from '../src/components/PersonName.vue'

it('人员标记按全名稳定配色，并保留姓名与空状态原文', () => {
  const name = '钟先睿、钟明、钟先睿、待分配'
  const wrapper = mount(PersonName, { props: { name } })
  const initials = wrapper.findAll('.pdm-person-initial')
  expect(initials.map(initial => initial.attributes('data-initial'))).toEqual(['钟', '钟', '钟'])
  expect(initials[0]?.attributes('style')).toBe(initials[2]?.attributes('style'))
  expect(initials[0]?.attributes('style')).not.toBe(initials[1]?.attributes('style'))
  expect(wrapper.text()).toBe(name)
})
