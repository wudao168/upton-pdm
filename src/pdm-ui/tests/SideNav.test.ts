import navBrandLogo from '../src/assets/upton-horizontal-brand-white.png'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SideNav from '../src/components/SideNav.vue'

describe('SideNav', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    document.body.innerHTML = ''
  })
  it('combines the project pages into one sidebar entry', async () => {
    const wrapper = mount(SideNav, { props: { active: 'project-center' } })
    const navigationLabels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())

    expect(navigationLabels).toEqual(['项目总览', '项目中心', '工时管理', '我的待办', '制造中心', '标准物料', '标准程序'])
    await wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item')[0]!.trigger('click')
    expect(wrapper.emitted('navigate')?.[0]).toEqual(['project-workbench', '项目总览'])
  })

  it('hides material management without material view permission', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects' } })

    expect(wrapper.text()).not.toContain('料品管理')
  })

  it('uses the concise material management label when permitted', () => {
    const wrapper = mount(SideNav, { props: { active: 'projects', canViewMaterials: true } })

    expect(wrapper.text()).toContain('料品管理')
    expect(wrapper.text()).not.toContain('料品与U9C')
  })

  it('combines standard resources into one sidebar entry', async () => {
    const wrapper = mount(SideNav, { props: { active: 'materials', canViewMaterials: true } })
    const labels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())

    expect(labels).toEqual(['项目总览', '项目中心', '工时管理', '我的待办', '制造中心', '标准物料', '标准程序', '料品管理'])
    expect(wrapper.findAll('.pdm-nav-group').map(group => ({
      label: group.attributes('aria-label'),
      entries: group.findAll('.pdm-nav-item').map(item => item.text().trim()),
    }))).toEqual([
      { label: '常用功能', entries: ['项目总览', '项目中心', '工时管理', '我的待办'] },
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

  it('opens detailed runtime information when the version is clicked', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ database: 'MySql', databaseName: 'pdm' }), { status: 200 })))
    const wrapper = mount(SideNav, {
      props: {
        active: 'projects',
        version: '2026.09.12.1710-version-information',
        desktopVersion: '2026.09.12.1700',
        solidWorksAddinVersion: '2026.09.12.1650',
        releaseHistory: [
          { Version: '2026.09.12.1710-version-information', ReleasedAt: '2026-09-12T17:10:00+08:00', ReleaseNote: '增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。' },
          { Version: '2026.09.11.1456-user-display-name', ReleasedAt: '2026-09-11T14:56:00+08:00', ReleaseNote: '完善用户显示名称。' },
        ],
      },
      global: { plugins: [ElementPlus] },
    })

    await wrapper.get('.pdm-sidebar__version').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('系统版本信息')
    expect(document.body.textContent).toContain('V2026.09.12.1710')
    expect(document.body.textContent).toContain('2026-09-12 17:10')
    expect(document.body.textContent).toContain('MySql · pdm')
    expect(document.body.textContent).toContain('V2026.09.12.1700')
    expect(document.body.textContent).toContain('V2026.09.12.1650')
    expect(document.body.textContent).toContain('增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。')
    expect(fetch).toHaveBeenCalledWith('/health', { cache: 'no-store' })

    const historyButton = Array.from(document.body.querySelectorAll('button')).find(button => button.textContent?.trim() === '版本记录')
    expect(historyButton?.getAttribute('aria-expanded')).toBe('false')
    historyButton?.click()
    await wrapper.vm.$nextTick()

    expect(historyButton?.getAttribute('aria-expanded')).toBe('true')
    const history = document.body.querySelector('[aria-label="版本记录"]')
    expect(history?.textContent).toContain('V2026.09.12.1710')
    expect(history?.textContent).toContain('2026-09-12 17:10')
    expect(history?.textContent).toContain('增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。')
    expect(history?.textContent).toContain('V2026.09.11.1456')
    expect(history?.textContent).toContain('完善用户显示名称。')
  })

  it('does not reuse an old hard-coded description for an untagged deployment version', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ database: 'MySql' }), { status: 200 })))
    const wrapper = mount(SideNav, {
      attachTo: document.body,
      props: { active: 'projects', version: '2026.09.13.1510' },
      global: { plugins: [ElementPlus] },
    })

    await wrapper.get('.pdm-sidebar__version').trigger('click')
    await flushPromises()
    const historyButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '版本记录')
    historyButton?.click()
    await wrapper.vm.$nextTick()

    expect(document.body.textContent).toContain('本次发布未填写版本说明。')
    expect(document.body.textContent).not.toContain('移除侧栏版本号前的“版本”文字')
    wrapper.unmount()
  })

  it('repairs UTF-8 release notes that were previously decoded as latin1', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ database: 'MySql' }), { status: 200 })))
    const wrapper = mount(SideNav, {
      attachTo: document.body,
      props: {
        active: 'projects',
        version: '2026.09.17.1303',
        releaseHistory: [
          { Version: '2026.09.17.1303', ReleasedAt: '2026-09-17T13:03:00+08:00', ReleaseNote: 'å®Œå–„BOMæ˜¾ç¤ºä¸Žæ’ä»¶æ›´æ–°ã€‚' },
        ],
      },
      global: { plugins: [ElementPlus] },
    })

    await wrapper.get('.pdm-sidebar__version').trigger('click')
    await flushPromises()
    const historyButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '版本记录')
    historyButton?.click()
    await wrapper.vm.$nextTick()

    expect(document.body.textContent).toContain('完善BOM显示与插件更新。')
    expect(document.body.textContent).not.toContain('å®Œå–„')
    wrapper.unmount()
  })

  it('lists a multi-item release note as numbered items', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ database: 'MySql' }), { status: 200 })))
    const wrapper = mount(SideNav, {
      attachTo: document.body,
      props: {
        active: 'projects',
        version: '2026.09.21.1840',
        releaseNote: '全局拖动条横向、纵向统一为 15px。\n版本说明按条列举。',
        releaseHistory: [
          { Version: '2026.09.21.1835', ReleasedAt: '2026-09-21T18:35:00+08:00', ReleaseNote: '横向拖动条加高到 20px；纵向滚动条保持原尺寸。' },
        ],
      },
      global: { plugins: [ElementPlus] },
    })

    await wrapper.get('.pdm-sidebar__version').trigger('click')
    await flushPromises()

    const note = document.body.querySelector('.pdm-system-version-detail > section .pdm-system-version-note')
    expect(note?.tagName).toBe('OL')
    expect(Array.from(note?.querySelectorAll('li') ?? []).map(item => item.textContent?.trim()))
      .toEqual(['全局拖动条横向、纵向统一为 15px。', '版本说明按条列举。'])

    const historyButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '版本记录')
    historyButton?.click()
    await wrapper.vm.$nextTick()

    const historyNote = document.body.querySelector('[aria-label="版本记录"] article:last-child .pdm-system-version-note')
    expect(Array.from(historyNote?.querySelectorAll('li') ?? []).map(item => item.textContent?.trim()))
      .toEqual(['横向拖动条加高到 20px', '纵向滚动条保持原尺寸。'])
    wrapper.unmount()
  })
})
