import { cloneVNode, defineComponent, Fragment, h, isVNode, mergeProps, nextTick, ref, type VNode, type VNodeChild } from 'vue'
import { useUserDisplayName, type UserDisplayNameResolver } from '../userDisplay'
import { ElOption, ElSelect, type SelectInstance } from 'element-plus'
import { matchesUserSearch } from '../userSearch'
import PersonName from './PersonName.vue'

function optionText(children: VNodeChild): string {
  if (typeof children === 'string' || typeof children === 'number') return String(children)
  if (Array.isArray(children)) return children.map(optionText).join('')
  return isVNode(children) ? optionText(children.children as VNodeChild) : ''
}

function filterOptions(nodes: VNode[], query: string, displayName: UserDisplayNameResolver): VNode[] {
  const personLabel = (value: unknown, label: string) =>
    typeof value === 'string' && value && (!label || label.trim().toLowerCase() === value.trim().toLowerCase())
      ? displayName(value) : label
  return nodes.flatMap(node => {
    if (node.type === Fragment && Array.isArray(node.children)) return filterOptions(node.children.filter(isVNode), query, displayName)
    if (node.type === 'option') {
      const text = optionText(node.children as VNodeChild)
      const value = node.props?.value ?? text
      const label = personLabel(node.props?.value, text)
      return matchesUserSearch(label, query, String(value))
        ? [h(ElOption, { ...node.props, key: node.key ?? undefined, value, label }, value ? { default: () => h(PersonName, { name: label }) } : undefined)] : []
    }
    if (node.type === ElOption || (typeof node.type === 'object' && (node.type as { name?: string }).name === 'ElOption')) {
      const value = node.props?.value
      const label = personLabel(value, String(node.props?.label ?? ''))
      if (!matchesUserSearch(label, query, String(value ?? ''))) return []
      if (typeof value !== 'string' || !value || node.children) return [cloneVNode(node, { label })]
      return [h(ElOption, { ...node.props, key: node.key ?? undefined, value, label }, { default: () => h(PersonName, { name: label }) })]
    }
    return [node]
  })
}

export default defineComponent({
  name: 'UserSelect',
  inheritAttrs: false,
  setup(_, { attrs, slots }) {
    const query = ref('')
    const select = ref<SelectInstance>()
    const displayName = useUserDisplayName()
    async function selected() {
      query.value = ''
      await nextTick()
      select.value?.blur()
    }
    return () => {
      const options = filterOptions(slots.default?.() ?? [], query.value, displayName)
      const placeholder = options.find(option => option.props?.value === '')?.props?.label ?? '请选择人员'
      return h(ElSelect, mergeProps({ placeholder, noMatchText: '没有匹配的人员', noDataText: '暂无可选人员' }, attrs, {
        ref: select,
        class: 'pdm-user-select',
        reserveKeyword: false,
        onChange: selected,
        filterable: true,
        filterMethod: (value: string) => { query.value = value },
        onVisibleChange: (visible: boolean) => { if (!visible) query.value = '' },
      }), { ...slots, label: slots.label ?? ((item: { label: string; value?: unknown }) => item.value ? h(PersonName, { name: item.label }) : item.label), default: () => options })
    }
  },
})
