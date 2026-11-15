import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SystemVersionInfo from '../src/components/SystemVersionInfo.vue'

describe('SystemVersionInfo', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('shows current and published versions and update history on entry', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ database: 'MySql', databaseName: 'pdm' })))
    const wrapper = mount(SystemVersionInfo, {
      props: {
        version: '2026.09.12.1710-version-information',
        desktopVersion: '2026.09.12.1700',
        publishedDesktopVersion: '2026.09.12.1705',
        solidWorksAddinVersion: '2026.09.12.1650',
        releaseHistory: [
          { Version: '2026.09.12.1710-version-information', ReleasedAt: '2026-09-12T17:10:00+08:00', ReleaseNote: '增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。' },
          { Version: '2026.09.11.1456-user-display-name', ReleasedAt: '2026-09-11T14:56:00+08:00', ReleaseNote: '完善用户显示名称。' },
        ],
      },
    })
    await flushPromises()

    const details = wrapper.get('[aria-label="系统版本信息"]').text()
    expect(details).toContain('V2026.09.12.1710')
    expect(details).toContain('2026-09-12 17:10')
    expect(details).toContain('MySql · pdm')
    expect(details).toContain('当前 Windows 客户端V2026.09.12.1700')
    expect(details).toContain('Windows 客户端发布版本V2026.09.12.1705')
    expect(details).toContain('SolidWorks 插件发布版本V2026.09.12.1650')
    expect(details).toContain('增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。')
    expect(fetch).toHaveBeenCalledWith('/health', { cache: 'no-store' })
    expect(wrapper.emitted('refresh')).toHaveLength(1)

    const history = wrapper.get('[aria-label="更新日志"]').text()
    expect(history).toContain('V2026.09.12.1710')
    expect(history).toContain('2026-09-12 17:10')
    expect(history).toContain('增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。')
    expect(history).toContain('V2026.09.11.1456')
    expect(history).toContain('完善用户显示名称。')
    wrapper.unmount()
  })

  it('uses a neutral message when the deployment has no release note', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ database: 'MySql' })))
    const wrapper = mount(SystemVersionInfo, { props: { version: '2026.09.13.1510' } })
    await flushPromises()
    expect(wrapper.text()).toContain('本次发布未填写版本说明。')
    expect(wrapper.text()).not.toContain('移除侧栏版本号前的“版本”文字')
    wrapper.unmount()
  })

  it('repairs UTF-8 release notes that were previously decoded as latin1', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ database: 'MySql' })))
    const wrapper = mount(SystemVersionInfo, {
      props: {
        version: '2026.09.17.1303',
        releaseHistory: [
          { Version: '2026.09.17.1303', ReleasedAt: '2026-09-17T13:03:00+08:00', ReleaseNote: 'å®Œå–„BOMæ˜¾ç¤ºä¸Žæ\u008f’ä»¶æ›´æ–°ã€‚' },
        ],
      },
    })
    await flushPromises()
    expect(wrapper.get('[aria-label="更新日志"]').text()).toContain('完善BOM显示与插件更新。')
    wrapper.unmount()
  })

  it('lists current and historical release notes as numbered items', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ database: 'MySql' })))
    const wrapper = mount(SystemVersionInfo, {
      props: {
        version: '2026.09.21.1840',
        releaseNote: '全局拖动条横向、纵向统一为 15px。\n版本说明按条列举。',
        releaseHistory: [
          { Version: '2026.09.21.1835', ReleasedAt: '2026-09-21T18:35:00+08:00', ReleaseNote: '横向拖动条加高到 20px；纵向滚动条保持原尺寸。' },
        ],
      },
    })
    await flushPromises()
    expect(wrapper.findAll('.pdm-system-version-detail ol li').map(item => item.text()))
      .toEqual(['全局拖动条横向、纵向统一为 15px。', '版本说明按条列举。'])
    expect(wrapper.findAll('[aria-label="更新日志"] article:last-child ol li').map(item => item.text()))
      .toEqual(['横向拖动条加高到 20px', '纵向滚动条保持原尺寸。'])
    wrapper.unmount()
  })
})
