<script setup lang="ts">
import { computed } from 'vue'
import type { DocumentNode } from '../types'

const props = withDefaults(defineProps<{
  kind: DocumentNode['kind']
  status?: DocumentNode['status']
  size?: number
}>(), {
  status: 'Normal',
  size: 16,
})

const issue = computed(() => props.status === 'Missing' || props.status === 'Unregistered')
const label = computed(() => ({
  Assembly: '装配体',
  Part: '零件',
  Drawing: '工程图',
  Pdf: 'PDF文件',
  Dwg: 'DWG文件',
  Other: '其他文件',
})[props.kind] ?? '图档')
</script>

<template>
  <svg
    class="pdm-cad-icon"
    :class="`is-${kind.toLowerCase()}`"
    :width="size"
    :height="size"
    viewBox="0 0 32 32"
    role="img"
    :aria-label="label"
    :data-cad-kind="kind"
  >
    <template v-if="kind === 'Assembly'">
      <g stroke-linejoin="round">
        <path d="M10.4 3.4 19.4 7.9 10.4 12.4 1.5 7.9Z" fill="#84b5d7" stroke="#284f72" stroke-width="1.05" />
        <path d="M1.5 7.9 10.4 12.4v9.7l-8.9-4.6Z" fill="#548fba" stroke="#284f72" stroke-width="1.05" />
        <path d="m10.4 12.4 9-4.5v9.6l-9 4.6Z" fill="#326d9b" stroke="#284f72" stroke-width="1.05" />
        <path d="m20.2 9.8 10.3 5.1-10.3 5.2-10.3-5.2Z" fill="#73a9d0" stroke="#234f72" stroke-width="1.1" />
        <path d="m9.9 14.9 10.3 5.2v8.5L9.9 23.4Z" fill="#3f7fae" stroke="#234f72" stroke-width="1.1" />
        <path d="m20.2 20.1 10.3-5.2v8.5l-10.3 5.2Z" fill="#275e8a" stroke="#234f72" stroke-width="1.1" />
      </g>
      <g data-cad-assembly-link="true">
        <path d="m11.2 15.6 7.5 3.7" fill="none" stroke="#246a9e" stroke-width="1.15" />
        <circle cx="9.8" cy="14.9" r="1.45" fill="#edf7ff" stroke="#285d8a" stroke-width=".9" />
        <circle cx="20.1" cy="20.1" r="1.45" fill="#edf7ff" stroke="#285d8a" stroke-width=".9" />
      </g>
    </template>
    <template v-else-if="kind === 'Part'">
      <g data-cad-part-body="true" stroke="#5b4310" stroke-width="1.15" stroke-linejoin="round">
        <path d="M16 3.3 27 8.8l-11 5.5L5 8.8Z" fill="#fad75b" />
        <path d="m5 8.8 11 5.5v12.9L5 21.6Z" fill="#e2a01a" />
        <path d="m16 14.3 11-5.5v12.8l-11 5.6Z" fill="#bf7605" />
      </g>
      <path d="m8 9.1 8 4 8-4" fill="none" stroke="#fff4bf" stroke-width=".7" />
    </template>
    <template v-else-if="kind === 'Drawing'">
      <path d="M5.2 2.6h15.6l6 6v20.8H5.2Z" fill="#fcfdff" stroke="#4d5662" stroke-width="1.2" stroke-linejoin="round" />
      <path d="M20.8 2.6v6h6" fill="#dce2e8" stroke="#4d5662" stroke-width="1.2" stroke-linejoin="round" />
      <rect x="8.3" y="11" width="7.8" height="6.2" rx=".4" fill="#e5eef7" stroke="#4776a0" stroke-width=".9" />
      <circle cx="21.3" cy="14.2" r="3.1" fill="none" stroke="#4d5662" stroke-width=".9" />
      <path d="M18.2 14.2h6.2m-3.1-3.1v6.2" stroke="#8a949f" stroke-width=".55" />
      <g data-cad-drawing-title-block="true">
        <path d="M8.3 20h15.5v6.1H8.3Z" fill="#f3f5f7" stroke="#68727d" stroke-width=".8" />
        <path d="M17.8 20v6.1M8.3 22.8h15.5m-3-2.8v6.1" fill="none" stroke="#7c8792" stroke-width=".65" />
      </g>
    </template>
    <template v-else>
      <path d="M5.2 2.6h15.6l6 6v20.8H5.2Z" fill="#e8edf2" stroke="#687787" stroke-width="1.2" stroke-linejoin="round" />
      <path d="M20.8 2.6v6h6" fill="#cdd7e0" stroke="#687787" stroke-width="1.2" stroke-linejoin="round" />
      <path d="M8.3 13h15m-15 5h15m-15 5h10" stroke="#8090a0" stroke-width="1.3" />
    </template>
    <g v-if="issue" data-cad-issue="true" transform="translate(20 20)">
      <circle cx="6" cy="6" r="5.4" fill="#fff" stroke="#d83838" stroke-width="1.2" />
      <path d="M6 2.7v4.2m0 2v.4" stroke="#d83838" stroke-linecap="round" stroke-width="1.35" />
    </g>
  </svg>
</template>
