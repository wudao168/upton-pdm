<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { fitSearchPlaceholder } from '../searchPlaceholder'
import '../styles/search-field.css'

const props = withDefaults(defineProps<{ disabled?: boolean; showAction?: boolean; compactPlaceholder?: boolean }>(), { showAction: true, compactPlaceholder: true })
const emit = defineEmits<{ search: [] }>()
const root = ref<HTMLElement>()
// 原生输入与 Element Plus 共用实际输入区域的宽度，保持输入值和无障碍名称完整。
const hints = new Map<HTMLInputElement, { original: string; displayed: string; originalTitle: string | null }>()
let resizeObserver: ResizeObserver | undefined
let mutationObserver: MutationObserver | undefined
let frame = 0
let disposed = false
let context: CanvasRenderingContext2D | null | undefined
function fitHints() {
  frame = 0
  if (!root.value || !props.compactPlaceholder) return
  for (const input of root.value.querySelectorAll<HTMLInputElement>('input[placeholder]')) {
    let hint = hints.get(input)
    if (!hint) {
      hint = { original: input.placeholder, displayed: input.placeholder, originalTitle: input.getAttribute('title') }
      hints.set(input, hint)
      resizeObserver?.observe(input)
    } else if (input.placeholder !== hint.displayed) {
      hint.original = input.placeholder
    }
    const style = getComputedStyle(input)
    const width = input.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0') - 4
    if (width <= 0) continue // 隐藏页签显示后由尺寸观察器重新计算。
    context ??= document.createElement('canvas').getContext('2d')
    if (!context) continue
    context.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`
    const spacing = parseFloat(style.letterSpacing) || 0
    hint.displayed = fitSearchPlaceholder(hint.original, width, text => context!.measureText(text).width + Math.max(0, text.length - 1) * spacing)
    if (input.placeholder !== hint.displayed) input.placeholder = hint.displayed
    if (hint.originalTitle === null) {
      if (hint.displayed !== hint.original) input.title = hint.original
      else input.removeAttribute('title')
    }
  }
  for (const input of hints.keys()) {
    if (!root.value.contains(input)) { resizeObserver?.unobserve(input); hints.delete(input) }
  }
}
function scheduleFit() {
  if (!disposed && !frame) frame = requestAnimationFrame(fitHints)
}
onMounted(() => {
  resizeObserver = new ResizeObserver(scheduleFit)
  resizeObserver.observe(root.value!)
  mutationObserver = new MutationObserver(scheduleFit)
  mutationObserver.observe(root.value!, { subtree: true, childList: true, attributes: true, attributeFilter: ['placeholder'] })
  fitHints()
  document.fonts?.ready.then(scheduleFit)
})
onBeforeUnmount(() => {
  disposed = true
  resizeObserver?.disconnect()
  mutationObserver?.disconnect()
  cancelAnimationFrame(frame)
})
function search() {
  const input = root.value?.querySelector('input')
  if (!input || input.disabled) return
  // Reapply the current input for live filters; explicit queries use the search event or action slot.
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.focus({ preventScroll: true })
  emit('search')
}
</script>

<template>
  <span ref="root" class="pdm-search-field" :class="{ 'is-disabled': disabled, 'has-query-action': !!$slots.action, 'is-input-only': !showAction }">
    <svg class="pdm-search-field__icon" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true"><path fill-rule="evenodd" d="M8 4a4 4 0 100 8 4 4 0 000-8zM2 8a6 6 0 1110.89 3.476l4.817 4.817a1 1 0 01-1.414 1.414l-4.816-4.816A6 6 0 012 8z" clip-rule="evenodd" /></svg>
    <span class="pdm-search-field__input"><slot /></span>
    <span v-if="showAction" class="pdm-search-field__action"><slot name="action"><button type="button" :disabled="disabled" @click="search">搜索</button></slot></span>
  </span>
</template>
