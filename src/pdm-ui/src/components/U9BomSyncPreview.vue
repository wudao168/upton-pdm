<script setup lang="ts">
import { computed } from 'vue'
import type { U9BomWritePreview } from '../types'

const props = defineProps<{ itemCode: string; componentCount: number; preview: U9BomWritePreview }>()
const changes = computed(() => props.preview.componentChanges ?? [])
const quantities = computed(() => props.preview.quantityReconciliations ?? [])
const count = (kind: string) => changes.value.filter(item => item.change === kind).length
const quantity = (value: number) => Number(value).toLocaleString('zh-CN', { maximumFractionDigits: 4 })
const delta = (value: number) => `${value > 0 ? '+' : ''}${quantity(value)}`
</script>

<template>
  <section class="u9-sync-preview">
    <div class="u9-sync-preview__summary">
      <span>母件：<strong>{{ itemCode }}</strong></span>
      <span>版本：<strong>A1</strong></span>
      <span>审核子件：<strong>{{ componentCount }}</strong></span>
    </div>
    <h3>本次变更 <small v-if="changes.length">新增 {{ count('新增') }} 行 · 修改 {{ count('修改') }} 行 · 删除 {{ count('删除') }} 行</small></h3>
    <div v-if="changes.length" class="u9-sync-preview__table-wrap">
      <table aria-label="本次子件变更">
        <thead><tr><th>操作</th><th>U9C项次</th><th>物料编码</th><th>变更前数量</th><th>变更后数量</th><th>增减数量</th></tr></thead>
        <tbody><tr v-for="item in changes" :key="`${item.sequence}-${item.itemCode}`">
          <td><span :class="['u9-sync-preview__change', { 'is-add': item.change === '新增', 'is-delete': item.change === '删除' }]">{{ item.change }}</span></td>
          <td>{{ item.sequence }}</td><td>{{ item.itemCode }}</td>
          <td>{{ quantity(item.previousQuantity) }}</td><td>{{ quantity(item.quantity) }}</td>
          <td>{{ delta(item.quantity - item.previousQuantity) }}</td>
        </tr></tbody>
      </table>
    </div>
    <p v-else>接口未提供逐行变更，请展开数量核对查看本次增减。</p>
    <details v-if="quantities.length" class="u9-sync-preview__reconciliation" :open="!changes.length">
      <summary>完整数量核对（{{ quantities.length }} 项，含未变化物料）</summary>
      <div class="u9-sync-preview__table-wrap">
        <table aria-label="完整数量核对">
          <thead><tr><th>物料编码</th><th>单位</th><th>母件底数</th><th>PLM审核总量</th><th>U9C现有总量</th><th>本次增减</th></tr></thead>
          <tbody><tr v-for="item in quantities" :key="`${item.itemCode}-${item.issueUomCode}-${item.parentQty}`">
            <td>{{ item.itemCode }}</td><td>{{ item.issueUomCode }}</td><td>{{ quantity(item.parentQty) }}</td>
            <td>{{ quantity(item.plmApprovedTotal) }}</td><td>{{ quantity(item.u9ExistingTotal) }}</td><td>{{ delta(item.uploadDelta) }}</td>
          </tr></tbody>
        </table>
      </div>
    </details>
    <ul class="u9-sync-preview__notes">
      <li>仅使用审核发布版本。仅调整历史审核记录核对通过的子件，其他U9C行保留，不删除整张BOM。</li>
      <li>减量/删除前记录快照；确认后立即执行并自动回查。U9C审核或引用限制不会自动绕过。</li>
    </ul>
  </section>
</template>

<style scoped>
.u9-sync-preview { width: 100%; color: var(--el-text-color-regular); }
.u9-sync-preview__summary { display: flex; flex-wrap: wrap; gap: 8px 24px; padding: 12px; background: var(--el-fill-color-light); border-radius: 4px; }
h3 { margin: 16px 0 10px; font-size: 14px; }
small { margin-left: 12px; font-weight: normal; color: var(--el-text-color-secondary); }
.u9-sync-preview__table-wrap { max-height: 320px; overflow: auto; border: 1px solid var(--el-border-color-lighter); }
table { width: 100%; min-width: 640px; border-collapse: separate; border-spacing: 0; text-align: center; font-size: 13px; }
th, td { padding: 9px 10px; border-bottom: 1px solid var(--el-border-color-lighter); white-space: nowrap; }
th { position: sticky; top: 0; z-index: 1; background: var(--el-fill-color-light); font-weight: 600; }
tbody tr:last-child td { border-bottom: 0; }
.u9-sync-preview__change { color: var(--el-color-warning); font-weight: 600; }
.u9-sync-preview__change.is-add { color: var(--el-color-success); }
.u9-sync-preview__change.is-delete { color: var(--el-color-danger); }
.u9-sync-preview__reconciliation { margin-top: 16px; }
summary { cursor: pointer; padding: 8px 0; color: var(--el-color-primary); }
.u9-sync-preview__notes { margin: 16px 0 0; padding-left: 20px; font-size: 12px; line-height: 1.7; }
</style>

<style>
.el-message-box.u9-bom-sync-confirm { width: min(960px, calc(100vw - 32px)); max-width: none; max-height: calc(100dvh - 32px); display: inline-flex; flex-direction: column; box-sizing: border-box; vertical-align: middle; }
.u9-bom-sync-confirm .el-message-box__header, .u9-bom-sync-confirm .el-message-box__btns { flex-shrink: 0; }
.u9-bom-sync-confirm .el-message-box__content { min-height: 0; overflow: auto; }
.u9-bom-sync-confirm .el-message-box__message { width: 100%; min-width: 0; }
</style>
