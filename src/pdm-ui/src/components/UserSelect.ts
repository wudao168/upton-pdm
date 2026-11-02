import { defineComponent, Fragment, h, isVNode, mergeProps, ref, type VNode, type VNodeChild } from 'vue'
import { ElOption, ElSelect } from 'element-plus'
import { matchesUserSearch } from '../userSearch'

function optionText(children: VNodeChild): string {
  if (typeof children === 'string' || typeof children === 'number') return String(children)
  if (Array.isArray(children)) return children.map(optionText).join('')
  return isVNode(children) ? optionText(children.children as VNodeChild) : ''
}

function filterOptions(nodes: VNode[], query: string): VNode[] {
  return nodes.flatMap(node => {
    if (node.type === Fragment && Array.isArray(node.children)) return filterOptions(node.children.filter(isVNode), query)
    if (node.type === 'option') {
      const label = optionText(node.children as VNodeChild)
      const value = node.props?.value ?? label
      return matchesUserSearch(label, query, String(value))
        ? [h(ElOption, { ...node.props, key: node.key ?? undefined, value, label })] : []
    }
    if (node.type === ElOption || (typeof node.type === 'object' && (node.type as { name?: string }).name === 'ElOption')) {
      return matchesUserSearch(String(node.props?.label ?? node.props?.value ?? ''), query, String(node.props?.value ?? '')) ? [node] : []
    }
    return [node]
  })
}

export default defineComponent({
  name: 'UserSelect',
  inheritAttrs: false,
  setup(_, { attrs, slots }) {
    const query = ref('')
    return () => {
      const options = filterOptions(slots.default?.() ?? [], query.value)
      const placeholder = options.find(option => option.props?.value === '')?.props?.label ?? '请选择人员'
      return h(ElSelect, mergeProps({ placeholder, noMatchText: '没有匹配的人员', noDataText: '暂无可选人员' }, attrs, {
        class: 'pdm-user-select',
        filterable: true,
        filterMethod: (value: string) => { query.value = value },
        onVisibleChange: (visible: boolean) => { if (!visible) query.value = '' },
      }), { ...slots, default: () => options })
    }
  },
})
