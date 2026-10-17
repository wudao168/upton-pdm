import { describe, expect, it } from 'vitest'
import { releaseFailureMessage } from '../src/releaseFailureMessage'

describe('转图启动错误提示', () => {
  it('把SolidWorks启动和RPC连接失败转换为中文处理建议', () => {
    expect(releaseFailureMessage('CLSID activation 80080005 CO_E_SERVER_EXEC_FAILURE')).toContain('无法启动 SolidWorks')
    expect(releaseFailureMessage('RPC server unavailable HRESULT 0x800706BA')).toContain('连接已中断')
    expect(releaseFailureMessage('RPC server unavailable HRESULT 0x800706BA')).not.toContain('unavailable')
  })
})
