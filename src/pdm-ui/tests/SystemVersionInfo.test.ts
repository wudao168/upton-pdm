import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SystemVersionInfo from '../src/components/SystemVersionInfo.vue'

describe('SystemVersionInfo', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    document.body.innerHTML = ''
  })

  it('opens detailed runtime information when the version is clicked', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ database: 'MySql', databaseName: 'pdm' }), { status: 200 })))
    const wrapper = mount(SystemVersionInfo, {
      props: {
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

    await wrapper.get('.pdm-system-version-trigger').trigger('click')
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
    const wrapper = mount(SystemVersionInfo, {
      attachTo: document.body,
      props: { version: '2026.09.13.1510' },
      global: { plugins: [ElementPlus] },
    })

    await wrapper.get('.pdm-system-version-trigger').trigger('click')
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
    const wrapper = mount(SystemVersionInfo, {
      attachTo: document.body,
      props: {
        version: '2026.09.17.1303',
        releaseHistory: [
          { Version: '2026.09.17.1303', ReleasedAt: '2026-09-17T13:03:00+08:00', ReleaseNote: 'å®Œå–„BOMæ˜¾ç¤ºä¸Žæ’ä»¶æ›´æ–°ã€‚' },
        ],
      },
      global: { plugins: [ElementPlus] },
    })

    await wrapper.get('.pdm-system-version-trigger').trigger('click')
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
    const wrapper = mount(SystemVersionInfo, {
      attachTo: document.body,
      props: {
        version: '2026.09.21.1840',
        releaseNote: '全局拖动条横向、纵向统一为 15px。\n版本说明按条列举。',
        releaseHistory: [
          { Version: '2026.09.21.1835', ReleasedAt: '2026-09-21T18:35:00+08:00', ReleaseNote: '横向拖动条加高到 20px；纵向滚动条保持原尺寸。' },
        ],
      },
      global: { plugins: [ElementPlus] },
    })

    await wrapper.get('.pdm-system-version-trigger').trigger('click')
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
