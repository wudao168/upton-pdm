import navBrandLogo from '../src/assets/upton-horizontal-brand-white.png'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import SideNav from '../src/components/SideNav.vue'

describe('SideNav', () => {
  it('未选中使用线框，选中时切换实心，菜单切换后同时更新两处图标', async () => {
    const wrapper = mount(SideNav, { props: { active: 'project-center', visibleKeys: ['project-center', 'production-drawings'] } })
    const items = wrapper.findAll('.pdm-nav-item')
    expect(items[0]!.get('svg').attributes('data-pdm-icon-weight')).toBe('fill')
    expect(items[1]!.get('svg').attributes('data-pdm-icon-weight')).toBe('regular')
    expect(items[1]!.get('svg').attributes('fill')).toBe('none')
    await wrapper.setProps({ active: 'production-drawings' })
    expect(items[0]!.get('svg').attributes('data-pdm-icon-weight')).toBe('regular')
    expect(items[0]!.get('svg').attributes('fill')).toBe('none')
    expect(items[1]!.get('svg').attributes('data-pdm-icon-weight')).toBe('fill')
    wrapper.unmount()
  })
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} unobserve() {} })
  })
  it('只显示可访问入口并移除空导航分组', async () => {
    const wrapper = mount(SideNav, { props: { active: 'tasks', visibleKeys: ['tasks', 'work-hours'], canViewMaterials: true, canManageSystem: true } })
    expect(wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text())).toEqual(['工时管理', '我的待办'])
    expect(wrapper.findAll('.pdm-nav-group')).toHaveLength(1)
    expect(wrapper.find('.pdm-sidebar__settings').exists()).toBe(false)
    await wrapper.setProps({ visibleKeys: ['tasks', 'production-drawings'] })
    expect(wrapper.findAll('.pdm-nav-group').map(item => item.attributes('aria-label'))).toEqual(['项目', '生产'])
    wrapper.unmount()
  })
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    document.body.innerHTML = ''
  })
  it('共用高亮从旧菜单移动并缩放到新菜单，减少动态效果时直接定位', async () => {
    let frame: FrameRequestCallback | undefined
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { frame = callback; return 1 })
    vi.stubGlobal('cancelAnimationFrame', () => { frame = undefined })
    const preference = { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() }
    vi.stubGlobal('matchMedia', () => preference)
    const animate = vi.fn((_frames: Keyframe[], _options: KeyframeAnimationOptions) => ({ cancel: vi.fn() }))
    vi.stubGlobal('Animation', class {})
    const originalAnimate = HTMLElement.prototype.animate
    HTMLElement.prototype.animate = animate as unknown as typeof HTMLElement.prototype.animate
    const rect = (x: number, y: number, width: number, height: number) => ({ x, y, left: x, top: y, width, height, right: x + width, bottom: y + height, toJSON: () => ({}) })
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      if (this.classList.contains('pdm-sidebar')) return rect(0, 0, 140, 800)
      if (this.tagName === 'NAV') return rect(0, 50, 140, 650)
      if (this.classList.contains('pdm-nav-item') && this.textContent?.includes('制造中心')) return rect(8, 250, 110, 50)
      return rect(8, 100, 124, 44)
    })
    const wrapper = mount(SideNav, { props: { active: 'project-center' } })
    try {
      frame?.(0)
      expect(animate).not.toHaveBeenCalled()
      expect((wrapper.get('.pdm-sidebar__highlight-layer').element as HTMLElement).style.clipPath).toBe('inset(26px 0 76px 0)')
      await wrapper.setProps({ active: 'production-drawings' })
      frame?.(16)
      expect(wrapper.findAll('.pdm-sidebar__highlight')).toHaveLength(1)
      expect(animate.mock.calls[0]?.[0]).toEqual([
        { transform: `translate(8px, 100px) scale(${124 / 110}, ${44 / 50})` },
        { transform: 'translate(8px, 250px)' },
      ])
      expect((wrapper.get('.pdm-sidebar__highlight').element as HTMLElement).style.width).toBe('110px')
      preference.matches = true
      await wrapper.setProps({ active: 'project-center' })
      frame?.(32)
      expect(animate).toHaveBeenCalledTimes(1)
    } finally {
      wrapper.unmount()
      HTMLElement.prototype.animate = originalAnimate
    }
  })
  it('combines the project pages into one sidebar entry', async () => {
    const wrapper = mount(SideNav, { props: { active: 'project-center', visibleKeys: ['project-workbench', 'project-center', 'work-hours', 'tasks', 'production-drawings', 'standard-library', 'program-templates'] } })
    const navigationLabels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())

    expect(navigationLabels).toEqual(['项目总览', '项目中心', '工时管理', '我的待办', '制造中心', '标准物料', '标准程序'])
    await wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item')[0]!.trigger('click')
    expect(wrapper.emitted('navigate')?.[0]).toEqual(['project-workbench', '项目总览'])
  })

  it('hides material management without material view permission', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects' } })

    expect(wrapper.text()).not.toContain('料品管理')
  })

  it('在工时管理显示未填工作日计数，归零后隐藏', async () => {
    const wrapper = mount(SideNav, { props: { active: 'work-hours', workHoursCount: 3 } })
    const item = wrapper.findAll('.pdm-nav-item').find(button => button.text().includes('工时管理'))!
    expect(item.get('em').text()).toBe('3')
    expect(item.attributes('title')).toBe('工时管理，3个工作日未填写工时')
    await wrapper.setProps({ workHoursCount: 0 })
    expect(item.find('em').exists()).toBe(false)
  })

  it('折叠后工时图标仍显示计数并提供完整提示', () => {
    const wrapper = mount(SideNav, { props: { active: 'work-hours', workHoursCount: 2, collapsed: true } })
    const item = wrapper.get('[aria-label="工时管理，2个工作日未填写工时"]')
    expect(item.get('em').text()).toBe('2')
  })

  it('uses the concise material management label when permitted', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects', canViewMaterials: true } })

    expect(wrapper.text()).toContain('料品管理')
    expect(wrapper.text()).not.toContain('料品与U9C')
  })

  it('combines standard resources into one sidebar entry', async () => {
    const wrapper = mount(SideNav, { props: { active: 'materials', canViewMaterials: true, visibleKeys: ['project-workbench', 'project-center', 'work-hours', 'tasks', 'production-drawings', 'standard-library', 'program-templates', 'materials'] } })
    const labels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())

    expect(labels).toEqual(['项目总览', '项目中心', '工时管理', '我的待办', '制造中心', '标准物料', '标准程序', '料品管理'])
    expect(wrapper.findAll('.pdm-nav-group').map(group => ({
      label: group.attributes('aria-label'),
      entries: group.findAll('.pdm-nav-item').map(item => item.text().trim()),
    }))).toEqual([
      { label: '项目', entries: ['项目总览', '项目中心', '工时管理', '我的待办'] },
      { label: '生产', entries: ['制造中心'] },
      { label: '标准化', entries: ['标准物料', '标准程序', '料品管理'] },
    ])
    await wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item')[5]!.trigger('click')
    expect(wrapper.emitted('navigate')?.[0]).toEqual(['standard-library', '标准物料'])
    await wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item')[6]!.trigger('click')
    expect(wrapper.emitted('navigate')?.[1]).toEqual(['program-templates', '标准程序'])
  })

  it('shows the combined material task count on material management', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects', materialCount: 3, canViewMaterials: true } })
    const materialItem = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').find(item => item.text().includes('料品管理'))!

    expect(materialItem.text()).toContain('料品管理')
    expect(materialItem.get('em').text()).toBe('3')
  })

  it('uses the uploaded horizontal lockup as a single sidebar brand image', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects' } })
    const brand = wrapper.get('.pdm-sidebar__brand')

    expect(brand.findAll('svg')).toHaveLength(1)
    expect(brand.get('image').attributes('href')).toBe(navBrandLogo)
    expect(brand.get('svg').attributes('viewBox')).toBe('0 0 1712 346')
    expect(brand.get('svg').attributes('aria-label')).toBe('UPTON')
  })

  it('replaces the former separate icon and text with the new lockup', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects' } })

    expect(wrapper.find('.pdm-sidebar__brand-copy').exists()).toBe(false)
    expect(wrapper.find('.pdm-sidebar__brand-mark').exists()).toBe(false)
    expect(wrapper.get('.pdm-sidebar__brand').text()).toBe('')
  })

  it('exposes navigation labels in collapsed mode', () => {
    const wrapper = mount(SideNav, { props: { active: 'materials', collapsed: true, canManageSystem: true, canViewMaterials: true } })

    expect(wrapper.get('.pdm-sidebar').classes()).toContain('is-collapsed')
    expect(wrapper.get('.pdm-sidebar__brand-logo').attributes('viewBox')).toBe('0 0 527 346')
    expect(wrapper.get('.pdm-sidebar__brand-logo image').attributes('href')).toBe(navBrandLogo)
    expect(wrapper.get('.pdm-nav-item[aria-label="料品管理"]').attributes('title')).toBe('料品管理')
    expect(wrapper.get('.pdm-sidebar__settings').attributes('title')).toBe('系统设置')
  })

  it('按授权在侧栏底部将 AI 助手放在系统设置上方', async () => {
    const wrapper = mount(SideNav, { props: {
      active: 'ai-assistant', visibleKeys: ['ai-assistant', 'purchase-analysis', 'admin'], canManageSystem: true,
    } })
    expect(wrapper.get('.pdm-nav-group').attributes('aria-label')).toBe('总结')
    expect(wrapper.get('.pdm-sidebar__nav').text()).not.toContain('AI助手')
    const footer = () => wrapper.findAll('.pdm-sidebar__footer button')
    expect(footer().map(button => button.text())).toEqual(['AI助手', '系统设置'])
    expect(footer()[0]!.attributes('aria-current')).toBe('page')
    await footer()[0]!.trigger('click')
    expect(wrapper.emitted('navigate')).toEqual([['ai-assistant', 'AI助手']])
    await wrapper.setProps({ canManageSystem: false })
    expect(footer().map(button => button.text())).toEqual(['AI助手'])
    await wrapper.setProps({ canManageSystem: true, visibleKeys: ['admin'] })
    expect(footer().map(button => button.text())).toEqual(['系统设置'])
    wrapper.unmount()
  })
})
