import { describe, expect, it } from 'vitest'
import { fitSearchPlaceholder } from '../src/searchPlaceholder'
const measure = (text: string) => text.length * 13

describe('搜索提示按可用宽度精简', () => {
  it('空间充足时保留完整提示，窄框保留图号和名称两个范围', () => {
    expect(fitSearchPlaceholder('搜索图号或名称', 200, measure)).toBe('搜索图号或名称')
    expect(fitSearchPlaceholder('搜索图号或名称', 70, measure)).toBe('图号/名称')
    expect(fitSearchPlaceholder('搜索图号或名称', 40, measure)).toBe('关键词')
  })
  it('长提示使用简短文字，隐藏搜索框稍后按实际宽度计算', () => {
    expect(fitSearchPlaceholder('搜索编码、名称、规格、品牌或分类', 200, measure)).toBe('编码/名称/规格/品牌/分类')
    expect(fitSearchPlaceholder('搜索编码、名称、规格、品牌或分类', 60, measure)).toBe('关键词')
    expect(fitSearchPlaceholder('搜索图号或名称', 0, measure)).toBe('搜索图号或名称')
  })
})
