import { describe, expect, it } from 'vitest'
import { releaseFailureMessage } from '../src/releaseFailureMessage'

describe('转图启动错误提示', () => {
  it('发布排序内存不足时说明中文原因并给出退回草稿的处理方式', () => {
    const message = releaseFailureMessage('Out of sort memory, consider increasing server sort buffer size')
    expect(message).toContain('排序内存不足')
    expect(message).toContain('退回草稿')
    expect(message).not.toContain('Out of sort memory')
  })
  it('把SolidWorks启动和RPC连接失败转换为中文处理建议', () => {
    expect(releaseFailureMessage('CLSID activation 80080005 CO_E_SERVER_EXEC_FAILURE')).toContain('无法启动 SolidWorks')
    expect(releaseFailureMessage('RPC server unavailable HRESULT 0x800706BA')).toContain('连接已中断')
    expect(releaseFailureMessage('RPC server unavailable HRESULT 0x800706BA')).not.toContain('unavailable')
  })
})
