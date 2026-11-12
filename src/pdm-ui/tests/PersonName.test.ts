import { mount } from '@vue/test-utils'
import { expect, it } from 'vitest'
import PersonName from '../src/components/PersonName.vue'
import CompanyName from '../src/components/CompanyName.vue'

it('人员标记按全名稳定配色，并保留姓名与空状态原文', () => {
  const name = '钟先睿、钟明、钟先睿、待分配'
  const wrapper = mount(PersonName, { props: { name } })
  const initials = wrapper.findAll('.pdm-person-initial')
  expect(initials.map(initial => initial.attributes('data-initial'))).toEqual(['钟', '钟', '钟'])
  expect(initials[0]?.attributes('style')).toBe(initials[2]?.attributes('style'))
  expect(initials[0]?.attributes('style')).not.toBe(initials[1]?.attributes('style'))
  expect(wrapper.text()).toBe(name)
})

it('公司全名中的标点保留原样，整家公司只显示一个首字块', async () => {
  const wrapper = mount(CompanyName, { props: { name: '昆山自动化，装备有限公司' } })
  expect(wrapper.findAll('.pdm-person-initial')).toHaveLength(1)
  expect(wrapper.get('.pdm-person-initial').attributes('data-initial')).toBe('昆')
  expect(wrapper.text()).toBe('昆山自动化，装备有限公司')

  await wrapper.setProps({ name: '请选择公司' })
  expect(wrapper.find('.pdm-person-initial').exists()).toBe(false)
  expect(wrapper.text()).toBe('请选择公司')
})
