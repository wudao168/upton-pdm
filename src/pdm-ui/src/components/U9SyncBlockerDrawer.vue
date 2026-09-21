<script setup lang="ts">
import type { U9SyncBlocker } from '../api'

const props = withDefaults(defineProps<{
  modelValue: boolean
  blockers: U9SyncBlocker[]
  title?: string
  /** true=需要用户决定（取消/仍然发布）；false=只读提醒（关闭）。 */
  decision?: boolean
}>(), { title: '', decision: false })

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  decide: [confirmed: boolean]
}>()

function close() { emit('update:modelValue', false) }
function decide(confirmed: boolean) {
  emit('decide', confirmed)
  close()
}
</script>

<template>
  <el-drawer
    class="pdm-u9-blocker-drawer"
    direction="rtl"
    size="620px"
    :model-value="props.modelValue"
    :title="props.title || `U9C BOM 未同步：${props.blockers.length} 个子件待补料号`"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <div v-if="props.modelValue" class="pdm-u9-blocker">
      <p class="pdm-u9-blocker__lead">
        U9C 只接收料号齐全的 BOM；以下 <strong>{{ props.blockers.length }}</strong> 个子件在当前已发布的 BOM 版本里还没有 U9C 正式料号：
      </p>
      <table class="pdm-u9-blocker__table">
        <thead>
          <tr><th>序号</th><th>分类</th><th>料号</th><th>名称</th><th>原因</th></tr>
        </thead>
        <tbody>
          <tr v-for="(item, index) in props.blockers" :key="`${item.category}-${item.sequence}-${item.materialCode}-${index}`">
            <td>{{ item.sequence }}</td>
            <td>{{ item.category }}</td>
            <td :class="{ 'is-muted': !item.materialCode }">{{ item.materialCode || '无料号' }}</td>
            <td>{{ item.name }}</td>
            <td>{{ item.reason }}</td>
          </tr>
        </tbody>
      </table>
      <p class="pdm-u9-blocker__path">
        处理路径：在 BOM 里补齐这些行（料号/分类/料品）并保存 → 在发布页重新发布这张 BOM（长交期发布或正式发布）→ 回到这里重试同步。
      </p>
    </div>
    <template #footer>
      <div class="pdm-u9-blocker__actions">
        <template v-if="props.decision">
          <button type="button" class="pdm-secondary-action" @click="decide(false)">取消</button>
          <button type="button" class="pdm-primary-action" @click="decide(true)">仍然发布</button>
        </template>
        <button v-else type="button" class="pdm-primary-action" @click="close">知道了</button>
      </div>
    </template>
  </el-drawer>
</template>

<style scoped>
.pdm-u9-blocker{display:flex;min-height:0;flex-direction:column;gap:12px}
.pdm-u9-blocker__lead{margin:0;color:var(--pdm-text-soft);line-height:1.6}
.pdm-u9-blocker__table{width:100%;border-collapse:collapse;table-layout:fixed}
.pdm-u9-blocker__table th,.pdm-u9-blocker__table td{padding:7px 8px;border-bottom:1px solid var(--pdm-border-soft);text-align:left;vertical-align:top;line-height:1.5}
.pdm-u9-blocker__table th{background:var(--pdm-surface-muted);color:var(--pdm-muted);font-weight:600}
.pdm-u9-blocker__table th:nth-child(1){width:56px}
.pdm-u9-blocker__table th:nth-child(2){width:72px}
.pdm-u9-blocker__table th:nth-child(3){width:120px}
.pdm-u9-blocker__table th:nth-child(4){width:150px}
.pdm-u9-blocker__table td.is-muted{color:var(--pdm-muted)}
.pdm-u9-blocker__empty{padding:24px;text-align:center;color:var(--pdm-muted)}
.pdm-u9-blocker__path{margin:0;padding:9px 10px;border-radius:5px;background:var(--pdm-blue-soft);color:var(--pdm-text-soft);line-height:1.6}
.pdm-u9-blocker__actions{display:flex;justify-content:flex-end;gap:8px}
</style>
