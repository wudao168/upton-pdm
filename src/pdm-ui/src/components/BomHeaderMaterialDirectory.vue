<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { listBomHeaderMaterialDirectory } from '../api'
import type { BomHeaderMaterialDirectoryItem } from '../types'

const props = defineProps<{ token: string }>()
const rows = ref<BomHeaderMaterialDirectoryItem[]>([])
const loading = ref(false)
const error = ref('')
const query = ref('')
const page = ref(1)
const kindLabels: Record<string, string> = { Master: '项目主BOM', Standard: '标准件BOM', NonStandard: '非标件BOM', Electrical: '电气BOM' }
const stateLabels: Record<string, string> = {
  NotRequested: '待发布／核对', Running: '自动处理中', ApprovalQueued: '自动审批排队中',
  WaitingRetry: '待核对／重试', Failed: '自动处理失败', Rejected: '历史申请已退回',
  Queued: '等待同步／回查', Completed: '已完成',
}
const filtered = computed(() => rows.value.filter(row => [row.projectCode, row.subprojectCode, row.projectName,
  row.materialCode, row.materialName, kindLabels[row.kind]].some(value => value.toLowerCase().includes(query.value.trim().toLowerCase()))))
const paged = computed(() => filtered.value.slice((page.value - 1) * 50, page.value * 50))
watch(query, () => { page.value = 1 })
async function load() {
  if (loading.value) return
  loading.value = true
  error.value = ''
  try {
    rows.value = await listBomHeaderMaterialDirectory(props.token)
    page.value = Math.min(page.value, Math.max(1, Math.ceil(filtered.value.length / 50)))
  } catch (reason) { error.value = reason instanceof Error ? reason.message : 'BOM表头料号加载失败' }
  finally { loading.value = false }
}
function openBom(row: BomHeaderMaterialDirectoryItem) {
  window.dispatchEvent(new CustomEvent('pdm-open-project', { detail: { projectId: row.projectId, tab: 'bom' } }))
}
onMounted(load)
</script>

<template>
  <section class="bom-header-directory" aria-label="BOM表头料号目录">
    <div class="bom-header-directory__toolbar">
      <p title="仅显示当前有权查看项目的BOM表头料号，与普通料品分开管理。系统自动批准及同步；历史记录不会因打开或刷新本页而自动重试。">仅显示当前有权查看项目的BOM表头料号，与普通料品分开管理。系统自动批准及同步；历史记录不会因打开或刷新本页而自动重试。</p>
      <el-input v-model="query" clearable placeholder="搜索项目、料号或BOM类型" aria-label="搜索BOM表头料号" />
      <el-button :loading="loading" @click="load">刷新</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-table v-loading="loading" :data="paged" stripe border height="100%" empty-text="暂无BOM表头料号" class="bom-header-directory__table">
      <el-table-column prop="projectCode" label="项目号" width="100" show-overflow-tooltip />
      <el-table-column prop="subprojectCode" label="子项目号" width="100" show-overflow-tooltip />
      <el-table-column prop="projectName" label="项目名称" width="150" show-overflow-tooltip />
      <el-table-column label="BOM类型" width="100" show-overflow-tooltip><template #default="{ row }">{{ kindLabels[row.kind] || row.kind }}</template></el-table-column>
      <el-table-column prop="materialCode" label="临时／正式料号" min-width="1" show-overflow-tooltip />
      <el-table-column prop="materialName" label="料品名称" min-width="1" show-overflow-tooltip />
      <el-table-column label="自动处理状态" width="100" show-overflow-tooltip><template #default="{ row }"><el-tag :type="row.automaticStatus === 'Completed' ? 'success' : ['Failed', 'Rejected'].includes(row.automaticStatus) ? 'danger' : 'info'">{{ stateLabels[row.automaticStatus] || row.automaticStatus }}</el-tag><span v-if="row.isArchived">（已停用）</span></template></el-table-column>
      <el-table-column prop="automaticMessage" label="处理说明／失败原因" min-width="1" show-overflow-tooltip />
      <el-table-column label="操作" width="150" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="openBom(row)">查看对应BOM</el-button></template></el-table-column>
    </el-table>
    <el-pagination v-model:current-page="page" :total="filtered.length" :page-size="50" layout="total, prev, pager, next" />
  </section>
</template>

<style scoped>
.bom-header-directory{height:100%;min-width:0;min-height:0;display:flex;flex-direction:column;gap:10px;overflow:hidden}
.bom-header-directory__toolbar{display:flex;align-items:center;gap:10px;min-width:0}
.bom-header-directory__toolbar p{flex:1;min-width:0;margin:0;color:var(--pdm-text-muted,#64748b);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.bom-header-directory__toolbar .el-input{flex:0 1 260px;min-width:0}
.bom-header-directory__table{flex:1;min-height:180px;width:100%;font-size:13px}
.bom-header-directory__table :deep(.cell){white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.bom-header-directory :deep(.el-pagination){justify-content:flex-end}
</style>
