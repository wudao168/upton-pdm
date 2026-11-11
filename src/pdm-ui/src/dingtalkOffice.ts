type DingTalkSdk = {
  env?: { platform?: string }
  ready: (callback: () => void) => void
  error?: (callback: () => void) => void
  requestAuthCode?: (input: { clientId: string; corpId: string; success: (result: { code: string }) => void; fail: () => void }) => void
  runtime?: { permission?: { requestAuthCode: (input: { corpId: string; onSuccess: (result: { code: string }) => void; onFail: () => void }) => void } }
}

export async function dingTalkOfficeAuthCode(corpId: string, appKey: string, bindingScan = false): Promise<string> {
  const target = window as Window & { dd?: DingTalkSdk }
  if (!target.dd) {
    await new Promise<void>((resolve, reject) => {
      const script = document.createElement('script')
      const timer = window.setTimeout(() => { script.remove(); reject(new Error('钉钉组件加载超时，请检查网络后重新进入。')) }, 15_000)
      script.src = 'https://g.alicdn.com/dingding/dingtalk-jsapi/3.0.25/dingtalk.open.js'
      script.onload = () => { clearTimeout(timer); resolve() }
      script.onerror = () => { clearTimeout(timer); script.remove(); reject(new Error('钉钉组件加载失败，请检查网络后重新进入。')) }
      document.head.appendChild(script)
    })
  }
  const sdk = target.dd
  if (!sdk?.env?.platform || sdk.env.platform === 'notInDingTalk') throw new Error(bindingScan
    ? '请使用本人钉钉的「扫一扫」打开绑定页面。' : '请在钉钉工作台中打开UPLM移动办公。')
  return new Promise<string>((resolve, reject) => {
    const timer = window.setTimeout(() => reject(new Error('钉钉身份识别超时，请重新进入应用。')), 15_000)
    const fail = () => { clearTimeout(timer); reject(new Error(bindingScan
      ? '钉钉身份识别失败，请重新扫码。若仍失败，请联系管理员核对应用的免登地址和可见范围。'
      : '钉钉身份识别失败，请从已发布的企业应用重新进入。')) }
    const success = (result: { code: string }) => { clearTimeout(timer); result.code ? resolve(result.code) : fail() }
    sdk.error?.(fail)
    sdk.ready(() => {
      try {
        if (sdk.requestAuthCode) sdk.requestAuthCode({ clientId: appKey, corpId, success, fail })
        else if (sdk.runtime?.permission) sdk.runtime.permission.requestAuthCode({ corpId, onSuccess: success, onFail: fail })
        else fail()
      } catch { fail() }
    })
  })
}
