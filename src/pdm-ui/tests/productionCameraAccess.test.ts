import { afterEach, describe, expect, it, vi } from 'vitest'
import { readProductionCameraAccess } from '../src/productionCameraAccess'
afterEach(() => vi.unstubAllGlobals())
describe('摄像头安全入口配置', () => {
  const value = { httpsScanUrl: 'https://192.168.2.8:5443/?productionScan=1', certificateUrl: './root.cer', androidCertificateUrl: './root.crt', certificateName: 'UPLM', certificateSha256: 'HASH' }
  it('读取内网配置，并阻止非HTTPS入口及非网页证书下载地址', async () => {
    const fetch = vi.fn().mockResolvedValue({ ok: true, json: async () => value }); vi.stubGlobal('fetch', fetch)
    expect(await readProductionCameraAccess()).toEqual(value)
    expect(fetch).toHaveBeenCalledWith(expect.any(URL), { cache: 'no-store' })
    fetch.mockResolvedValue({ ok: true, json: async () => ({ ...value, httpsScanUrl: 'http://192.168.2.8:5173/' }) })
    await expect(readProductionCameraAccess()).rejects.toThrow('安全扫码地址配置有误')
    fetch.mockResolvedValue({ ok: true, json: async () => ({ ...value, certificateUrl: 'javascript:alert(1)' }) })
    await expect(readProductionCameraAccess()).rejects.toThrow('扫码证书下载地址配置有误')
  })
  it('没有配置时说明缺少HTTPS入口', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }))
    await expect(readProductionCameraAccess()).rejects.toThrow('请联系系统管理员配置内网HTTPS')
  })
})
