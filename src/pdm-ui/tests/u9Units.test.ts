import { describe, expect, it } from 'vitest'
import { u9UnitLabel, u9UnitName, u9UnitOptions } from '../src/u9Units'

describe('U9C计量单位目录', () => {
  it('使用确认的001至013编码和名称显示', () => {
    expect(u9UnitOptions).toEqual([
      { code: '001', name: '个' },
      { code: '002', name: '台' },
      { code: '003', name: '个' },
      { code: '004', name: '盒' },
      { code: '005', name: '卷' },
      { code: '006', name: '捆' },
      { code: '007', name: '双' },
      { code: '008', name: '片' },
      { code: '009', name: '桶' },
      { code: '010', name: '支' },
      { code: '011', name: '组' },
      { code: '012', name: '箱' },
      { code: '013', name: '包' },
    ])
    expect(u9UnitLabel('001')).toBe('001 个')
    expect(u9UnitLabel('013')).toBe('013 包')
    expect(u9UnitName('001')).toBe('个')
    expect(u9UnitName('013')).toBe('包')
  })
})
