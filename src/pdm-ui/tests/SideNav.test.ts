import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SideNav from '../src/components/SideNav.vue'

describe('SideNav', () => {
  it('places the project list directly below the project center', async () => {
    const wrapper = mount(SideNav, { props: { active: 'project-center' } })
    const navigationLabels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())

    expect(navigationLabels.slice(0, 2)).toEqual(['项目中心', '项目列表'])
    await wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item')[1]!.trigger('click')
    expect(wrapper.emitted('navigate')?.[0]).toEqual(['projects', '项目列表'])
  })

  it('uses the concise material management label', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects' } })

    expect(wrapper.text()).toContain('料品管理')
    expect(wrapper.text()).not.toContain('料品与U9C')
  })

  it('uses the multi-resolution desktop icon for the web and desktop shell brand', () => {
    const wrapper = mount(SideNav, {
      props: {
        active: 'projects',
      },
    })

    const brandMark = wrapper.get('.pdm-sidebar__brand-mark')
    expect(brandMark.attributes('src')).toContain('PdmClient.ico')
    expect(brandMark.attributes('width')).toBe('38')
    expect(brandMark.attributes('height')).toBe('38')
    expect(brandMark.attributes('draggable')).toBe('false')
  })
})
