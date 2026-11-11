import { describe, expect, it } from 'vitest'
import { genieBandTransform } from '../src/drawerGenieMath'
function project(width: number, top: number, p: number, x: number, y: number) {
  const m = genieBandTransform(width, 960, top, 32, p).slice(9, -1).split(',').map(Number)
  const denominator = m[3]! * x + m[7]! * y + m[15]!
  return { x: (m[0]! * x + m[4]! * y + m[12]!) / denominator, y: (m[1]! * x + m[5]! * y + m[13]!) / denominator }
}
describe('抽屉连续曲面投影', () => {
  it('展开终点恢复原内容位置与尺寸', () => {
    expect(project(720, 320, 0, 270, 18)).toEqual({ x: 270, y: 338 })
  })
  it('全过程相邻条带边界连续，且不翻转或产生无穷值', () => {
    for (const width of [300, 720, 1200]) for (let frame = 0; frame <= 32; frame++) {
      const p = frame / 32
      for (let top = 0; top < 928; top += 32) {
        for (const x of [0, width / 2, width]) {
          const bottom = project(width, top, p, x, 32), next = project(width, top + 32, p, x, 0)
          expect(bottom.x).toBeCloseTo(next.x, 8)
          expect(bottom.y).toBeCloseTo(next.y, 8)
          expect(Number.isFinite(bottom.x + bottom.y)).toBe(true)
        }
        const left = project(width, top, p, 0, 0), right = project(width, top, p, width, 0), lower = project(width, top, p, 0, 32)
        expect(right.x).toBeGreaterThan(left.x)
        expect(lower.y).toBeGreaterThan(left.y)
      }
    }
  })
  it('中间阶段上下宽度不同，形成弯曲而非整体缩放', () => {
    expect(project(720, 800, .6, 0, 0).x - project(720, 0, .6, 0, 0).x).toBeGreaterThan(50)
  })
  it('收拢终点位于原面板右下方', () => {
    const top = project(720, 0, 1, 0, 0), edge = project(720, 0, 1, 720, 0)
    expect(top.x).toBeCloseTo(720 * .88 + 36)
    expect(top.y).toBe(996)
    expect(edge.x).toBe(756)
  })
})
