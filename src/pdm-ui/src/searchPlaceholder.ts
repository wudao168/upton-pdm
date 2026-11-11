/** 保留搜索范围；仅在完整提示放不下时使用简短提示。 */
export function fitSearchPlaceholder(text: string, availableWidth: number, measure: (text: string) => number) {
  if (availableWidth <= 0) return text
  const compact = text.replace(/^(?:请输入|输入|搜索|查找|筛选)\s*/, '').replace(/(?:、|，|,|或)/g, '/')
  return [text, compact, '关键词'].find(candidate => candidate && measure(candidate) <= availableWidth) ?? '关键词'
}
