import { describe, expect, it } from 'vitest'
import { matchesUserSearch } from '../src/userSearch'

describe('人员搜索', () => {
  it.each([
    ['张明', 'zm', true], ['张明', ' ZM ', true], ['张明', 'z m', true],
    ['钟先鲁', 'zxl', true], ['张明', 'wm', false], ['张明', '张', true],
    ['张明', '明', true], ['曾乐', 'zl', true], ['单明', 'sm', true],
    ['欧阳明', 'oym', true], ['王一', 'wy', true], ['李昂', 'la', true],
    ['Alex Wang', 'alex', true], ['张明', '', true], ['张明', 'z.*', false],
  ])('%s 匹配 %s → %s', (name, query, expected) => {
    expect(matchesUserSearch(name, query)).toBe(expected)
  })
  it('preserves case-insensitive account search without changing labels', () => {
    expect(matchesUserSearch('张明', 'ENGINEER.01', 'engineer.01')).toBe(true)
    expect(matchesUserSearch('张明（engineer.01）', 'zm')).toBe(true)
  })
})
