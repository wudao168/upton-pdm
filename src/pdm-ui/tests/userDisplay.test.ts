import { describe, expect, it } from 'vitest'
import { resolveUserDisplayName } from '../src/userDisplay'

const users = [
  { username: 'liupengbo', displayName: '刘鹏搏' },
] as any

describe('resolveUserDisplayName', () => {
  it('业务界面优先显示姓名，并为未知账号保留可识别兜底', () => {
    expect(resolveUserDisplayName(users, 'LIUPENGBO')).toBe('刘鹏搏')
    expect(resolveUserDisplayName(users, 'unknown-user')).toBe('unknown-user')
    expect(resolveUserDisplayName(users, '', '待分配')).toBe('待分配')
  })
})
