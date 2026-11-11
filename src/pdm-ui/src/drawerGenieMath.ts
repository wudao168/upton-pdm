/** 将每一水平条带投影到梯形；相邻条带共用边界，形成连续的神奇收拢效果。 */
const clamp = (value: number) => Math.max(0, Math.min(1, value))
const smooth = (value: number) => { const t = clamp(value); return t * t * t * (t * (t * 6 - 15) + 10) }
export function genieEdge(width: number, height: number, y: number, progress: number) {
  const p = clamp(progress), fraction = y / height
  const contraction = smooth(Math.pow(p, 1.6))
  const bend = contraction + contraction * (1 - contraction) * .9 * fraction * fraction
  const vertical = smooth((p - .18) / .82)
  return { left: (width * .88 + 36) * bend, right: width + 36 * bend, y: y * (1 - vertical) + (height + 36 + y * .02) * vertical }
}
export function genieBandTransform(width: number, height: number, top: number, bandHeight: number, progress: number) {
  const upper = genieEdge(width, height, top, progress), lower = genieEdge(width, height, top + bandHeight, progress)
  const upperWidth = upper.right - upper.left, lowerWidth = lower.right - lower.left
  const denominator = upperWidth / lowerWidth, g = (denominator - 1) / bandHeight
  const a = upperWidth / width, b = (lower.left * denominator - upper.left) / bandHeight
  const d = (lower.y * denominator - upper.y) / bandHeight
  return `matrix3d(${a},0,0,0,${b},${d},0,${g},0,0,1,0,${upper.left},${upper.y},0,1)`
}