<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { Megaphone, Pause, Play } from '@lucide/vue'
import type { ProjectActivity } from '../projectActivity'
const props = defineProps<{ items: ProjectActivity[] }>()
const emit = defineEmits<{ open: [item: ProjectActivity] }>()
const selectedId = ref('')
const hovered = ref(false)
const focused = ref(false)
const paused = ref(false)
const index = computed(() => Math.max(0, props.items.findIndex(item => item.id === selectedId.value)))
const current = computed(() => props.items[index.value])
const label = computed(() => current.value ? current.value.projectCode + '｜' + current.value.title + ' · ' + current.value.content : '')
let timer: ReturnType<typeof setInterval> | undefined
function next() {
  if (paused.value || hovered.value || focused.value || document.hidden || props.items.length < 2) return
  selectedId.value = props.items[(index.value + 1) % props.items.length]!.id
}
watch(() => props.items, () => {
  if (!props.items.some(item => item.id === selectedId.value)) selectedId.value = props.items[0]?.id || ''
}, { immediate: true })
function time(value: string) { return new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', hour: '2-digit', minute: '2-digit' }).format(new Date(value)) }
onMounted(() => { timer = setInterval(next, 6000) })
onUnmounted(() => { if (timer) clearInterval(timer) })
</script>

<template>
  <div v-if="current" class="pdm-global-status pdm-project-activity" :class="{ 'is-important': current.important }"
    aria-label="项目动态轮播" @mouseenter="hovered = true" @mouseleave="hovered = false"
    @focusin="focused = true" @focusout="focused = false">
    <span class="pdm-project-activity__label"><Megaphone :size="14" />项目动态</span>
    <button type="button" class="pdm-project-activity__main" :title="label" :aria-label="label + '，点击查看详情'" @click="emit('open', current)">
      <Transition name="activity-slide" mode="out-in">
        <span :key="current.id" class="pdm-project-activity__line">
          <strong>{{ current.projectCode }}｜{{ current.title }}</strong>
          <span class="pdm-project-activity__summary">{{ current.content }}</span>
          <time :datetime="current.createdAt">{{ time(current.createdAt) }}</time>
        </span>
      </Transition>
    </button>
    <span class="pdm-project-activity__count">{{ index + 1 }}/{{ items.length }}</span>
    <button v-if="items.length > 1" type="button" class="pdm-project-activity__toggle" :aria-label="paused ? '继续轮播' : '暂停轮播'" :title="paused ? '继续轮播' : '暂停轮播'" @click="paused = !paused">
      <Play v-if="paused" :size="13" /><Pause v-else :size="13" />
    </button>
  </div>
</template>

<style scoped>
.pdm-project-activity { display: flex; align-items: center; gap: 8px; min-width: 0; overflow: hidden; padding: 0 8px; line-height: 24px; }
.pdm-project-activity__label { display: inline-flex; align-items: center; gap: 4px; flex: 0 0 auto; color:var(--pdm-text); font-size: 12px; }
.pdm-project-activity__main { flex: 1; min-width: 0; height: 26px; overflow: hidden; border: 0; padding: 0; background: transparent; color: inherit; cursor: pointer; text-align: left; }
.pdm-project-activity__line { display: flex; align-items: center; gap: 8px; min-width: 0; height: 26px; white-space: nowrap; }
.pdm-project-activity__line strong { flex: 0 1 auto; min-width: 0; max-width: 75%; overflow: hidden; text-overflow: ellipsis; font-size: 12px; font-weight: 500; }
.pdm-project-activity__summary { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; font-size: 12px; opacity: .8; }
.pdm-project-activity time, .pdm-project-activity__count { flex: 0 0 auto; font-size: 11px; opacity: .65; }
.pdm-project-activity__toggle { display: inline-flex; align-items: center; justify-content: center; flex: 0 0 22px; width: 22px; height: 22px; padding: 0; border: 0; background: transparent; color:var(--pdm-text); cursor: pointer; }
.activity-slide-enter-active, .activity-slide-leave-active { transition: transform .2s ease, opacity .2s ease; }
.activity-slide-enter-from { opacity: 0; transform: translateY(100%); }
.activity-slide-leave-to { opacity: 0; transform: translateY(-100%); }
@media (max-width: 1100px) { .pdm-project-activity__summary, .pdm-project-activity time { display: none; } .pdm-project-activity__line strong { max-width: 100%; } }
@media (prefers-reduced-motion: reduce) { .activity-slide-enter-active, .activity-slide-leave-active { transition: none; } }
</style>
