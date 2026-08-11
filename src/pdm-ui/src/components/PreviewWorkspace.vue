<script setup lang="ts">
import { Boxes, Maximize, MoreHorizontal } from '@lucide/vue'
import type { BomItem, DocumentNode, PreviewMode } from '../types'

defineProps<{ selected: DocumentNode; bom: BomItem[] }>()
const mode = defineModel<PreviewMode>('mode', { required: true })
const emit = defineEmits<{ open: [node: DocumentNode]; fit: []; more: [] }>()

const tabs: Array<{ value: PreviewMode; label: string }> = [
  { value: 'model', label: '3D预览' },
  { value: 'drawing', label: '2D图纸' },
  { value: 'bom', label: '机械BOM' },
]
</script>

<template>
  <section class="pdm-panel pdm-preview-panel" aria-label="图档预览">
    <header class="pdm-preview-toolbar">
      <div class="pdm-view-tabs" role="tablist" aria-label="资料视图">
        <button
          v-for="tab in tabs"
          :key="tab.value"
          type="button"
          role="tab"
          :aria-selected="mode === tab.value"
          @click="mode = tab.value"
        >{{ tab.label }}</button>
      </div>
      <div class="pdm-preview-actions">
        <button type="button" aria-label="适合窗口" @click="emit('fit')"><Maximize :size="15" /></button>
        <button type="button" aria-label="更多操作" @click="emit('more')"><MoreHorizontal :size="17" /></button>
      </div>
    </header>

    <div class="pdm-selected-bar">
      <span class="pdm-selected-bar__title"><Boxes :size="16" /><strong>{{ selected.drawingNumber }} {{ selected.name }}</strong></span>
      <span>{{ selected.fileName }}</span>
      <span>工作版本 <b>{{ selected.version }}</b></span>
      <button type="button" class="pdm-selected-status" @click="emit('open', selected)">{{ selected.checkedOutBy ? `正在编辑 · ${selected.checkedOutBy}` : '打开图档' }}</button>
    </div>

    <div class="pdm-preview-content">
      <div v-if="mode === 'model'" class="pdm-model-view">
        <svg viewBox="0 0 640 350" role="img" aria-labelledby="cad-title cad-desc">
          <title id="cad-title">自动装配线三维模型预览</title>
          <desc id="cad-desc">展示机架、输送带、机械臂和电气柜的简化三维模型。</desc>
          <defs>
            <linearGradient id="machine-top" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#dce8f3" /><stop offset="1" stop-color="#aebfd0" /></linearGradient>
            <linearGradient id="machine-side" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#91a9be" /><stop offset="1" stop-color="#6f879d" /></linearGradient>
          </defs>
          <g class="cad-grid"><path d="M58 282H586M73 260H571M91 238H553M112 216H532" /><path d="M89 300L218 190M175 300L273 190M261 300L328 190M347 300L383 190M433 300L438 190M519 300L493 190" /></g>
          <ellipse class="cad-shadow" cx="325" cy="280" rx="235" ry="30" />
          <g class="cad-machine">
            <path class="cad-side" d="M120 191L478 191L520 219L160 219Z" />
            <path class="cad-top" d="M120 177L478 177L520 205L160 205Z" />
            <path class="cad-edge" d="M120 177V191L160 219V205M478 177V191L520 219V205" />
            <path class="cad-rail" d="M154 185L477 185L492 195L170 195Z" />
            <path class="cad-belt" d="M180 188L455 188L468 196L193 196Z" />
            <path class="cad-edge" d="M145 207V274H161V215M477 207V274H493V215M268 211V263H283V212M388 211V263H403V212M134 274H173M465 274H504M257 263H294M377 263H414" />
          </g>
          <g class="cad-robot">
            <path class="cad-side" d="M270 167L318 167L331 177L283 177Z" />
            <path class="cad-top" d="M278 145L310 145L318 167L270 167Z" />
            <circle cx="294" cy="143" r="12" /><path d="M294 143L339 116L351 127L309 153Z" /><circle cx="345" cy="121" r="10" /><path d="M350 117L385 91L397 101L355 128Z" /><circle cx="391" cy="96" r="8" /><path d="M396 94L414 104M411 99L420 109" />
          </g>
          <g class="cad-cabinet">
            <path class="cad-side" d="M83 136L135 153V239L83 220Z" /><path class="cad-top" d="M83 136L112 122L164 139L135 153Z" /><path class="cad-cabinet-front" d="M135 153L164 139V224L135 239Z" />
            <circle cx="148" cy="160" r="3" /><circle cx="148" cy="171" r="3" /><path d="M141 184L156 179V207L141 212Z" />
          </g>
          <g class="cad-parts"><path d="M204 176L231 167L253 174L228 183Z" /><path d="M375 176L402 167L424 174L399 183Z" /></g>
          <g class="cad-dimension"><path d="M112 300H519M112 294V306M519 294V306" /><text x="315" y="320" text-anchor="middle">总长 4200 mm</text></g>
        </svg>
      </div>

      <div v-else-if="mode === 'drawing'" class="pdm-drawing-view">
        <svg viewBox="0 0 640 350" role="img" aria-label="A01-000总装配二维工程图">
          <rect x="38" y="24" width="564" height="302" class="drawing-frame" />
          <g class="drawing-lines"><path d="M104 115H320V212H104ZM124 96H300V115M140 212V238M286 212V238M95 238H329M95 232V244M329 232V244" /><circle cx="154" cy="164" r="24" /><circle cx="270" cy="164" r="24" /><path d="M380 90H546V218H380ZM402 112H524V196H402ZM380 238H546V302H380ZM455 238V302M505 238V302M380 263H546" /></g>
          <g class="drawing-text"><text x="195" y="258">4200</text><text x="390" y="253">图号</text><text x="463" y="253">{{ selected.drawingNumber }}</text><text x="390" y="281">名称</text><text x="463" y="281">{{ selected.name }}</text></g>
        </svg>
      </div>

      <div v-else class="pdm-bom-view">
        <el-table :data="bom" size="small" height="310" stripe>
          <el-table-column prop="sequence" label="序号" width="60" />
          <el-table-column prop="drawingNumber" label="图号" min-width="110" />
          <el-table-column prop="name" label="名称" min-width="110" />
          <el-table-column prop="quantity" label="数量" width="70" />
          <el-table-column prop="material" label="材料" min-width="80" />
          <el-table-column prop="revision" label="版本" width="66" />
        </el-table>
      </div>
    </div>
  </section>
</template>
