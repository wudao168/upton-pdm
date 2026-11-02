/** v-show 页面保留编辑状态，隐藏期间不参与全局表现增强。 */
export function isInactiveDom(element: Element) {
  return Boolean(element.closest('[hidden], [aria-hidden="true"], [style*="display: none"], [style*="display:none"]'))
}
