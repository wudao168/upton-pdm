<script setup lang="ts">
import { computed } from 'vue'
import type { ProgramTemplateParameter } from '../types'

const props = defineProps<{
  code: string
  name: string
  version: string
  parameters: ProgramTemplateParameter[]
}>()

const leftPins = computed(() => props.parameters
  .filter(item => item.direction === 'Input' || item.direction === 'InOut')
  .sort((left, right) => left.sortOrder - right.sortOrder))
const rightPins = computed(() => props.parameters
  .filter(item => item.direction === 'Output')
  .sort((left, right) => left.sortOrder - right.sortOrder))
const rowCount = computed(() => Math.max(leftPins.value.length, rightPins.value.length, 1))
const height = computed(() => 112 + rowCount.value * 32)
const blockBottom = computed(() => height.value - 18)
const blockHeight = computed(() => blockBottom.value - 18)
const pinY = (index: number) => 92 + index * 32
const short = (value: string, length = 20) => value.length > length ? `${value.slice(0, length - 1)}…` : value
</script>

<template>
  <svg class="program-block-diagram" :viewBox="`0 0 640 ${height}`" role="img" :aria-label="`${name}功能块接口图`">
    <defs>
      <filter id="program-block-shadow" x="-20%" y="-20%" width="140%" height="150%">
        <feDropShadow dx="0" dy="5" stdDeviation="7" flood-color="#1f5fbf" flood-opacity=".12" />
      </filter>
    </defs>

    <rect x="205" y="18" width="230" :height="blockHeight" rx="13" class="program-block-diagram__body" filter="url(#program-block-shadow)" />
    <text x="320" y="44" text-anchor="middle" class="program-block-diagram__title">{{ short(name, 24) }}</text>
    <text x="320" y="62" text-anchor="middle" class="program-block-diagram__meta">{{ code }} · {{ version }}</text>
    <line x1="225" y1="72" x2="415" y2="72" class="program-block-diagram__divider" />

    <g v-for="(parameter, index) in leftPins" :key="parameter.id" :class="parameter.direction === 'InOut' ? 'is-inout' : 'is-input'">
      <title>{{ parameter.name }} · {{ parameter.dataType }}{{ parameter.description ? ` · ${parameter.description}` : '' }}</title>
      <line x1="68" :y1="pinY(index)" x2="205" :y2="pinY(index)" class="program-block-diagram__wire" />
      <circle cx="205" :cy="pinY(index)" r="3.5" class="program-block-diagram__pin" />
      <text x="194" :y="pinY(index) - 4" text-anchor="end" class="program-block-diagram__name">{{ short(parameter.name, 17) }}</text>
      <text x="194" :y="pinY(index) + 12" text-anchor="end" class="program-block-diagram__type">{{ short(parameter.dataType, 16) }}</text>
      <text x="58" :y="pinY(index) + 4" text-anchor="end" class="program-block-diagram__direction">{{ parameter.direction === 'InOut' ? '↔' : '→' }}</text>
    </g>

    <g v-for="(parameter, index) in rightPins" :key="parameter.id" class="is-output">
      <title>{{ parameter.name }} · {{ parameter.dataType }}{{ parameter.description ? ` · ${parameter.description}` : '' }}</title>
      <line x1="435" :y1="pinY(index)" x2="572" :y2="pinY(index)" class="program-block-diagram__wire" />
      <circle cx="435" :cy="pinY(index)" r="3.5" class="program-block-diagram__pin" />
      <text x="446" :y="pinY(index) - 4" class="program-block-diagram__name">{{ short(parameter.name, 17) }}</text>
      <text x="446" :y="pinY(index) + 12" class="program-block-diagram__type">{{ short(parameter.dataType, 16) }}</text>
      <text x="582" :y="pinY(index) + 4" class="program-block-diagram__direction">→</text>
    </g>

    <text x="225" :y="blockBottom - 10" class="program-block-diagram__caption">INPUT / INOUT</text>
    <text x="415" :y="blockBottom - 10" text-anchor="end" class="program-block-diagram__caption">OUTPUT</text>
  </svg>
</template>

<style scoped>
.program-block-diagram { width: 100%; min-width: 520px; height: auto; display: block; font-family: Inter, "Microsoft YaHei", sans-serif; font-size: 12px; }
.program-block-diagram__body { fill: #f4f8ff; stroke: #1f64c8; stroke-width: 2.5; }
.program-block-diagram__title { fill: #17365f; font-size: 12px; font-weight: 700; }
.program-block-diagram__meta { fill: #7085a3; font-size: 12px; font-family: Consolas, monospace; }
.program-block-diagram__divider { stroke: #cbd8ea; stroke-width: 1; }
.program-block-diagram__wire { fill: none; stroke-width: 2; }
.program-block-diagram__pin { stroke-width: 1.5; }
.program-block-diagram__name { font-size: 12px; font-weight: 700; }
.program-block-diagram__type { fill: #778aa5; font-size: 12px; font-family: Consolas, monospace; }
.program-block-diagram__direction { font-size: 12px; font-weight: 700; }
.program-block-diagram__caption { fill: #94a3b8; font-size: 12px; font-weight: 700; letter-spacing: .06em; }
.is-input .program-block-diagram__wire,.is-input .program-block-diagram__pin { stroke: #0aa79b; }.is-input .program-block-diagram__pin { fill: #dff8f4; }.is-input .program-block-diagram__name,.is-input .program-block-diagram__direction { fill: #08857c; }
.is-output .program-block-diagram__wire,.is-output .program-block-diagram__pin { stroke: #ee8a13; }.is-output .program-block-diagram__pin { fill: #fff0db; }.is-output .program-block-diagram__name,.is-output .program-block-diagram__direction { fill: #c86c00; }
.is-inout .program-block-diagram__wire,.is-inout .program-block-diagram__pin { stroke: #7c55d9; }.is-inout .program-block-diagram__pin { fill: #eee7ff; }.is-inout .program-block-diagram__name,.is-inout .program-block-diagram__direction { fill: #6540bd; }
</style>
