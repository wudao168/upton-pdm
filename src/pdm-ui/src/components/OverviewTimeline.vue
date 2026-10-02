<script setup lang="ts">
import { computed } from 'vue'
import type { ProjectPlan } from '../types'

const props = defineProps<{ plan: ProjectPlan | null; baseline: ProjectPlan | null; baselineUnavailable: boolean; progressTone?: 'success' | 'warning' | 'danger' }>()
const emit = defineEmits<{ openTask: [taskId?: string] }>()
const phases = [
  { name: '设计', codes: ['Design'], color: '#3b82f6' },
  { name: '备料', codes: ['MaterialPreparation'], color: '#8b5cf6' },
  { name: '装配', codes: ['Assembly'], color: '#14b8a6' },
  { name: '调试', codes: ['Commissioning', 'ClientCommissioning'], color: '#f59e0b' },
  { name: '验收', codes: ['AcceptanceProgress', 'FinalAcceptance'], color: '#22a35a' },
]
const today = new Date().toLocaleDateString('sv-SE')
const progressLabel = computed(() => props.progressTone === 'danger' ? '已滞后' : props.progressTone === 'warning' ? '有风险' : props.progressTone === 'success' ? '正常' : '进度状态未知')
const day = (date: string) => Date.parse(`${date.slice(0, 10)}T00:00:00Z`) / 86400000
const baselineTasks = computed(() => props.baseline?.tasks ?? [])
const bounds = computed(() => {
  const dates = [...[...(props.plan?.tasks ?? []), ...baselineTasks.value]
    .flatMap(task => [task.plannedStart, task.plannedFinish, task.baselineStart, task.baselineFinish, task.actualStart, task.actualFinish]), props.plan?.plannedStart, props.plan?.plannedFinish].filter((date): date is string => Boolean(date))
  if (!dates.length) return null
  const values = dates.map(day)
  if (props.plan?.tasks.some(task => task.actualStart && !task.actualFinish)) values.push(day(today))
  return { start: Math.min(...values), finish: Math.max(...values) + 1 }
})
function position(date: string) {
  const range = bounds.value!
  return Math.max(0, Math.min(100, (day(date) - range.start) / (range.finish - range.start) * 100))
}
const timeTicks = computed(() => {
  const range = bounds.value
  if (!range) return []
  const last = range.finish - 1
  const span = last - range.start
  const dates = [range.start]
  for (let value = range.start + 7; value < last; value += 7) dates.push(value)
  if (span > 0) dates.push(last)
  const crossesYear = new Date(range.start * 86400000).getUTCFullYear() !== new Date(last * 86400000).getUTCFullYear()
  return dates
    .map(value => {
      const date = new Date(value * 86400000).toISOString().slice(0, 10)
      return { date, label: value !== last && value !== range.start && last - value < 4 ? '' : crossesYear ? date : date.slice(5), left: (value - range.start) / (range.finish - range.start) * 100 }
    })
})
const rows = computed(() => ['actual', 'planned', 'baseline'].map(kind => {
  const tasks = kind === 'baseline' ? baselineTasks.value : props.plan?.tasks ?? []
  const segments = phases.flatMap(phase => {
    const members = tasks.filter(task => phase.codes.includes(task.stage))
    const starts = members.map(task => kind === 'actual' ? task.actualStart : kind === 'baseline' ? task.baselineStart : task.plannedStart).filter((date): date is string => Boolean(date)).sort()
    const finishes = members.map(task => kind === 'actual' ? task.actualStart ? task.actualFinish || today : undefined : kind === 'baseline' ? task.baselineFinish : task.plannedFinish).filter((date): date is string => Boolean(date)).sort()
    if (!starts.length || !finishes.length || !bounds.value) return []
    const start = starts[0]!, finish = finishes[finishes.length - 1]!
    const weight = members.reduce((sum, task) => sum + Math.max(1, task.weight || 1), 0)
    const completion = Math.round(members.reduce((sum, task) => sum + Math.max(1, task.weight || 1) * task.completionPercent, 0) / weight)
    if (kind === 'actual' && completion <= 0) return []
    const plannedStarts = members.map(task => task.plannedStart).filter((date): date is string => Boolean(date)).sort()
    const plannedFinishes = members.map(task => task.plannedFinish).filter((date): date is string => Boolean(date)).sort()
    const barStart = kind === 'actual' ? plannedStarts[0] || start : start
    const barFinish = kind === 'actual' ? plannedFinishes[plannedFinishes.length - 1] || finish : finish
    const fullWidth = Math.max(.6, position(barFinish) - position(barStart) + 100 / (bounds.value.finish - bounds.value.start))
    const taskId = [...members].sort((a, b) => a.sortOrder - b.sortOrder)[0]?.id
    return [{ ...phase, start, finish, completion, taskId, left: position(barStart), width: fullWidth * (kind === 'actual' ? Math.min(100, completion) / 100 : 1) }]
  })
  return { kind, label: kind === 'actual' ? '实际进度' : kind === 'planned' ? '当前计划' : '初始基线', segments,
    empty: kind === 'actual' ? '未开始 / 未填报实际日期' : kind === 'baseline' ? props.baselineUnavailable ? '初始基线读取失败或历史缺失' : '未建立基线' : '未排程' }
}))
function segmentBackground(segment: { left: number; width: number; color: string }, segments: Array<{ left: number; width: number }>) {
  const end = segment.left + segment.width
  const overlaps = segments.filter(other => other !== segment && other.left < end && other.left + other.width > segment.left)
    .map(other => ({ start: Math.max(segment.left, other.left), end: Math.min(end, other.left + other.width) }))
  if (!overlaps.length) return segment.color
  const points = [...new Set([segment.left, end, ...overlaps.flatMap(range => [range.start, range.end])])].sort((a, b) => a - b)
  const stops = points.slice(0, -1).flatMap((start, index) => {
    const finish = points[index + 1]!
    const midpoint = (start + finish) / 2
    const color = overlaps.some(range => midpoint >= range.start && midpoint < range.end)
      ? `color-mix(in srgb, ${segment.color} 65%, transparent)` : segment.color
    return [`${color} ${(start - segment.left) / segment.width * 100}%`, `${color} ${(finish - segment.left) / segment.width * 100}%`]
  })
  return `linear-gradient(to right, ${stops.join(', ')})`
}
</script>

<template>
  <div class="pdm-overview-timeline" aria-label="三行项目进度">
    <div class="pdm-overview-timeline__chart">
      <div v-for="row in rows" :key="row.kind" class="pdm-overview-timeline__row" :aria-label="row.label">
        <strong>{{ row.label }}</strong>
        <div class="pdm-overview-timeline__track">
          <span v-if="!row.segments.length" class="pdm-overview-timeline__empty" :class="{ 'has-truck': row.kind === 'actual' }">{{ row.empty }}</span>
          <button v-for="segment in row.segments" :key="segment.name" class="pdm-overview-timeline__segment" :class="{ 'is-baseline': row.kind === 'baseline' }" :style="{ left: `${segment.left}%`, width: `${Math.min(segment.width, 100 - segment.left)}%`, background: segmentBackground(segment, row.segments) }" :title="`${segment.name}主任务 · ${segment.start} — ${segment.finish}${row.kind === 'actual' ? ` · 完成 ${segment.completion}%` : ''}`" @click="emit('openTask', segment.taskId)"><span>● {{ segment.name }}{{ row.kind === 'actual' ? ` ${segment.completion}%` : '' }}</span></button>
          <i v-if="(row.kind === 'planned' || row.kind === 'actual' && row.segments.length) && bounds && day(today) >= bounds.start && day(today) < bounds.finish" class="pdm-overview-timeline__today-arrow" :class="props.progressTone && `is-${props.progressTone}`" :style="{ left: `${position(today)}%` }" aria-label="当前日期位置" :title="`${today} · ${progressLabel}`" />
          <div v-if="row.kind === 'actual'" class="pdm-overview-timeline__truck" :style="{ left: `min(calc(${Math.min(100, Math.max(0, ...row.segments.map(segment => segment.left + segment.width)))}% + 4px), calc(100% - 34px))` }" role="img" :aria-label="`${progressLabel}，货车行驶中`" :title="progressLabel">
            <div class="pdm-overview-truck__wrapper" aria-hidden="true">
              <svg class="pdm-overview-truck__body" viewBox="0 0 130 70"><rect x="1" y="5" width="81" height="49" rx="4" fill="#008c95" /><path d="M86 22H108L124 41V59H86Z" fill="#f59e0b" /><path d="M94 27H106L117 41H94Z" fill="#dbeafe" /><path d="M1 59H128" stroke="#282828" stroke-width="7" /><rect x="119" y="47" width="8" height="5" rx="1" fill="#fff" /></svg>
              <div class="pdm-overview-truck__tires"><svg v-for="wheel in 2" :key="wheel" viewBox="0 0 24 24"><circle cx="12" cy="12" r="11" fill="#282828" /><circle cx="12" cy="12" r="5" fill="#d1d5db" /><circle cx="12" cy="12" r="2" fill="#6b7280" /></svg></div>
              <svg class="pdm-overview-truck__lamp" viewBox="0 0 45 90"><path d="M9 90V12Q9 4 17 4H37" fill="none" stroke="#64748b" stroke-width="4" /><path d="M27 4H43V11H27Z" fill="#fbbf24" /></svg>
              <div class="pdm-overview-truck__road" />
            </div>
          </div>
        </div>
      </div>
    </div>
    <div v-if="bounds" class="pdm-overview-timeline__dates" aria-label="计划时间刻度">
      <div class="pdm-overview-timeline__axis">
        <span v-for="(tick, index) in timeTicks" :key="tick.date" class="pdm-overview-timeline__tick" :class="{ 'is-first': index === 0, 'is-last': index === timeTicks.length - 1 && index > 0 }" :style="{ left: `${tick.left}%` }" :title="tick.date"><time :datetime="tick.date">{{ tick.label }}</time></span>
      </div>
    </div>
  </div>
</template>
