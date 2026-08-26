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

  it('places the permission-gated standard structure directly below standard materials', async () => {
    const wrapper = mount(SideNav, { props: { active: 'materials', canViewStandardLibrary: true } })
    const labels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())

    expect(labels.slice(2, 5)).toEqual(['标准物料', '标准结构', '料品管理'])
    await wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item')[3]!.trigger('click')
    expect(wrapper.emitted('navigate')?.[0]).toEqual(['standard-structure', '标准结构'])
  })

  it('shows the combined material task count on material management', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects', materialCount: 3 } })
    const materialItem = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item')[2]!

    expect(materialItem.text()).toContain('料品管理')
    expect(materialItem.get('em').text()).toBe('3')
  })

  it('uses the six-face animated PLM cube for the web and desktop shell brand', () => {
    const wrapper = mount(SideNav, {
      props: {
        active: 'projects',
      },
    })

    const brandMark = wrapper.get('.pdm-sidebar__brand-mark')
    expect(brandMark.element.tagName).toBe('SPAN')
    expect(brandMark.attributes('aria-hidden')).toBe('true')
    expect(brandMark.findAll('.plm-cube-icon__face').map(face => face.text())).toEqual(['P', 'L', 'M', '阿', '普', '顿'])
    expect(brandMark.findAll('.plm-cube-icon__tile')).toHaveLength(24)
    expect(brandMark.findAll('.plm-cube-icon__label.is-latin').map(label => label.text())).toEqual(['P', 'L', 'M'])
  })

  it('uses the UPTON wordmark beside the cube', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects' } })

    const logo = wrapper.get('.pdm-sidebar__brand-logo')
    expect(logo.attributes('src')).toContain('upton-logo-white.png')
    expect(logo.attributes('alt')).toBe('UPTON')
    expect(wrapper.get('.pdm-sidebar__brand-copy').text()).toBe('')
  })

  it('exposes navigation labels in collapsed mode', () => {
    const wrapper = mount(SideNav, { props: { active: 'materials', collapsed: true, canManageSystem: true } })

    expect(wrapper.get('.pdm-sidebar').classes()).toContain('is-collapsed')
    expect(wrapper.get('.pdm-nav-item[aria-label="料品管理"]').attributes('title')).toBe('料品管理')
    expect(wrapper.get('.pdm-sidebar__settings').attributes('title')).toBe('系统管理')
  })
})
