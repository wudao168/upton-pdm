<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, onUpdated, ref } from 'vue'
import { footerNavigationEntries, mainNavigationGroups } from '../mainNavigation'
import navBrandLogo from '../assets/upton-horizontal-brand-white.png'
import type { NavigationKey } from '../navigationAccess'

type NavKey = NavigationKey

const props = withDefaults(defineProps<{
  active: NavKey
  visibleKeys?: readonly string[]
  approvalCount?: number
  materialCount?: number
  workHoursCount?: number
  canManageSystem?: boolean
  canViewMaterials?: boolean
  collapsed?: boolean
  token?: string
}>(), {
  approvalCount: 0,
  materialCount: 0,
  workHoursCount: 0,
  canManageSystem: false,
  canViewMaterials: false,
  collapsed: false,
  token: '',
})
const emit = defineEmits<{ navigate: [key: NavKey, label: string] }>()
const sidebar = ref<HTMLElement>()
const highlightLayer = ref<HTMLElement>()
const highlight = ref<HTMLElement>()
let highlightFrame = 0
let highlightAnimation: Animation | undefined
let highlightObserver: ResizeObserver | undefined
let motionPreference: MediaQueryList | undefined
let lastHighlightedKey: NavKey | undefined
let lastHighlightGeometry = ''

function updateHighlight() {
  highlightFrame = 0
  const root = sidebar.value, layer = highlightLayer.value, block = highlight.value
  if (!root || !layer || !block) return
  const target = root.querySelector<HTMLElement>('.pdm-nav-item.is-active')
  const bounds = target?.getBoundingClientRect()
  if (!target || !bounds?.width || !bounds.height) {
    highlightAnimation?.cancel()
    layer.hidden = true
    root.classList.remove('has-moving-highlight')
    lastHighlightedKey = undefined
    lastHighlightGeometry = ''
    return
  }
  const rootBounds = root.getBoundingClientRect()
  const x = bounds.left - rootBounds.left, y = bounds.top - rootBounds.top
  const width = bounds.width
  const navBounds = target.closest('nav')?.getBoundingClientRect()
  const geometry = `${x},${y},${width},${bounds.height},${rootBounds.height},${navBounds?.top},${navBounds?.bottom}`
  if (geometry === lastHighlightGeometry && lastHighlightedKey === props.active && !motionPreference?.matches) return
  const previous = !layer.hidden ? block.getBoundingClientRect() : undefined
  const transform = `translate(${x}px, ${y}px)`
  highlightAnimation?.cancel()
  Object.assign(block.style, { transform, width: `${width}px`, height: `${bounds.height}px` })
  layer.style.clipPath = navBounds ? `inset(${Math.max(0, navBounds.top - rootBounds.top - 24)}px 0 ${Math.max(0, rootBounds.bottom - navBounds.bottom - 24)}px 0)` : ''
  layer.hidden = false
  root.classList.add('has-moving-highlight')
  if (previous?.width && lastHighlightedKey && lastHighlightedKey !== props.active && !motionPreference?.matches) {
    highlightAnimation = block.animate([
      { transform: `translate(${previous.left - rootBounds.left}px, ${previous.top - rootBounds.top}px) scale(${previous.width / width}, ${previous.height / bounds.height})` },
      { transform },
    ], { duration: 500, easing: 'cubic-bezier(.22,1.16,.36,1)' })
  }
  lastHighlightedKey = props.active
  lastHighlightGeometry = geometry
}
function scheduleHighlight() {
  cancelAnimationFrame(highlightFrame)
  highlightFrame = requestAnimationFrame(updateHighlight)
}
onMounted(() => {
  motionPreference = window.matchMedia('(prefers-reduced-motion: reduce)')
  motionPreference.addEventListener('change', scheduleHighlight)
  highlightObserver = new ResizeObserver(scheduleHighlight)
  if (sidebar.value) {
    highlightObserver.observe(sidebar.value)
    sidebar.value.querySelectorAll('.pdm-sidebar__brand, .pdm-sidebar__nav, .pdm-nav-group, .pdm-sidebar__footer').forEach(element => highlightObserver!.observe(element))
    sidebar.value.addEventListener('scroll', scheduleHighlight, true)
  }
  window.addEventListener('resize', scheduleHighlight)
  scheduleHighlight()
})
onUpdated(scheduleHighlight)
onBeforeUnmount(() => {
  cancelAnimationFrame(highlightFrame)
  highlightAnimation?.cancel()
  highlightObserver?.disconnect()
  motionPreference?.removeEventListener('change', scheduleHighlight)
  sidebar.value?.removeEventListener('scroll', scheduleHighlight, true)
  window.removeEventListener('resize', scheduleHighlight)
})
const workHoursHint = computed(() => props.workHoursCount > 0 ? `工时管理，${props.workHoursCount}个工作日未填写工时` : '工时管理')
const visibleGroups = computed(() => mainNavigationGroups.map(group => ({ ...group, items: group.items.filter(item =>
  (!props.visibleKeys || props.visibleKeys.includes(item.key)) && (item.key !== 'materials' || props.canViewMaterials)) })).filter(group => group.items.length))
const footerItems = computed(() => footerNavigationEntries.filter(item =>
  (!props.visibleKeys || props.visibleKeys.includes(item.key)) && (item.key !== 'admin' || props.canManageSystem)))
</script>

<template>
  <aside ref="sidebar" class="pdm-sidebar" :class="{ 'is-collapsed': props.collapsed }" aria-label="主导航">
    <div ref="highlightLayer" class="pdm-sidebar__highlight-layer" aria-hidden="true" hidden><span ref="highlight" class="pdm-sidebar__highlight" /></div>
    <div class="pdm-sidebar__brand">
      <svg class="pdm-sidebar__brand-logo" :viewBox="props.collapsed ? '0 0 527 346' : '0 0 1712 346'" role="img" aria-label="UPTON" overflow="hidden">
        <image :href="navBrandLogo" width="1712" height="346" />
      </svg>
    </div>
    <nav class="pdm-sidebar__nav">
      <div v-for="group in visibleGroups" :key="group.key" class="pdm-nav-group" role="group" :aria-label="group.label || '常用功能'">
        <p v-if="group.label" class="pdm-nav-group__label" aria-hidden="true">{{ group.label }}</p>
        <button
          v-for="item in group.items"
          :key="item.label"
          type="button"
          class="pdm-nav-item"
          :class="{ 'is-active': props.active === item.key }"
          :style="{ '--pdm-nav-icon-color': item.iconColor }"
          :aria-current="props.active === item.key ? 'page' : undefined"
          :aria-label="props.collapsed ? item.key === 'work-hours' ? workHoursHint : item.label : undefined"
          :title="item.key === 'work-hours' && props.workHoursCount > 0 ? workHoursHint : props.collapsed ? item.label : undefined"
          @click="emit('navigate', item.key, item.label)"
        >
          <component :is="item.icon" :filled="props.active === item.key" :size="14" :style="{ color: item.iconColor }" aria-hidden="true" />
          <span>{{ item.label }}</span>
          <em v-if="item.key === 'materials' && props.materialCount">{{ props.materialCount }}</em>
          <em v-else-if="item.key === 'tasks' && props.approvalCount">{{ props.approvalCount }}</em>
          <em v-else-if="item.key === 'work-hours' && props.workHoursCount > 0" :aria-label="`${props.workHoursCount}个工作日未填写工时`">{{ props.workHoursCount }}</em>
        </button>
      </div>
    </nav>
    <div v-if="footerItems.length" class="pdm-sidebar__footer">
      <button
        v-for="item in footerItems"
        :key="item.key"
        type="button"
        class="pdm-nav-item"
        :class="{ 'is-active': props.active === item.key, 'pdm-sidebar__settings': item.key === 'admin' }"
        :aria-current="props.active === item.key ? 'page' : undefined"
        :aria-label="props.collapsed ? item.label : undefined"
        :title="props.collapsed ? item.label : undefined"
        @click="emit('navigate', item.key, item.label)"
      >
        <component :is="item.icon" :filled="props.active === item.key" :size="14" :style="{ color: item.iconColor }" aria-hidden="true" />
        <span>{{ item.label }}</span>
      </button>
    </div>
  </aside>
</template>

<style scoped>
.pdm-sidebar__highlight-layer { position: absolute; inset: 0; z-index: 0; pointer-events: none; }
.pdm-sidebar__highlight { position: absolute; left: 0; top: 0; transform-origin: top left; border: 1px solid #d1d1d7; border-radius: 8px; background: var(--pdm-surface); box-shadow: 0 1px 2px rgb(0 0 0 / 4%); }
.pdm-sidebar__nav, .pdm-sidebar__footer { position: relative; z-index: 1; }
:root body .pdm-sidebar.has-moving-highlight .pdm-nav-item.is-active { border-color: transparent !important; background: transparent !important; box-shadow: none !important; }
</style>
