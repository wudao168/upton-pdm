import { afterEach, describe, expect, it, vi } from 'vitest'
import { copyText } from '../src/copyText'
import { ElMessage } from '../src/statusMessage'
vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn() } }))
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })
describe('复制业务文本', () => {
  it('HTTPS保留前导零和型号字符', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    await copyText('00102-Φ5', '型号')
    expect(writeText).toHaveBeenCalledWith('00102-Φ5')
    expect(ElMessage.success).toHaveBeenCalledWith('已复制型号')
  })
  it('HTTP或剪贴板被拒绝时使用选中复制，并清理临时输入', async () => {
    vi.stubGlobal('navigator', { clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) } })
    let copied = ''
    Object.defineProperty(document, 'execCommand', { configurable: true, value: vi.fn(() => { copied = (document.activeElement as HTMLTextAreaElement).value; return true }) })
    await copyText('UKIT-000001', '料号')
    expect(copied).toBe('UKIT-000001')
    expect(document.querySelector('textarea[aria-hidden="true"]')).toBeNull()
  })
  it('复制失败时显示中文操作建议', async () => {
    vi.stubGlobal('navigator', {})
    Object.defineProperty(document, 'execCommand', { configurable: true, value: vi.fn(() => false) })
    await copyText('00123', '料号')
    expect(ElMessage.warning).toHaveBeenCalledWith('自动复制未成功，请选中内容后按 Ctrl+C 复制。')
  })
})
