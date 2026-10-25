export interface ProductionCameraAccess {
  httpsScanUrl: string
  certificateUrl: string
  androidCertificateUrl: string
  certificateName: string
  certificateSha256: string
}

export async function readProductionCameraAccess(): Promise<ProductionCameraAccess> {
  const response = await fetch(new URL('production-camera-access.json', document.baseURI), { cache: 'no-store' })
  if (!response.ok) throw new Error('安全扫码入口尚未配置，请联系系统管理员配置内网HTTPS。')
  const value = await response.json() as ProductionCameraAccess
  const secure = new URL(value.httpsScanUrl)
  if (secure.protocol !== 'https:' || secure.username || secure.password) throw new Error('安全扫码地址配置有误，请联系系统管理员检查HTTPS地址。')
  for (const path of [value.certificateUrl, value.androidCertificateUrl])
    if (!['http:', 'https:'].includes(new URL(path, document.baseURI).protocol)) throw new Error('扫码证书下载地址配置有误，请联系系统管理员检查。')
  return value
}
