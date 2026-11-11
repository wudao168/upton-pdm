<script setup lang="ts">
import { useUserDisplayName as usePersonDisplayName } from '../userDisplay'
const displayUserName = usePersonDisplayName()
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { listRecycleArchiveBatches, previewRecycleArchive, recycleArchiveSet, listArchiveRecycleSets, restoreArchiveSet } from '../api'
import type { ArchiveRecycleBatch, ArchiveRecyclePreview, ArchiveRecycleSet } from '../api'

const props = defineProps<{ projectId: string; token: string; onReload: () => Promise<unknown> }>()
const open = defineModel<boolean>('open', { default: false })
const batches = ref<ArchiveRecycleBatch[]>([])
const sets = ref<ArchiveRecycleSet[]>([])
const batchId = ref('')
const preview = ref<ArchiveRecyclePreview>()
const reason = ref('')
const confirmed = ref(false)
const busy = ref(false)
const error = ref('')
let sequence = 0
const canRecycle = computed(() => preview.value?.canRecycle && reason.value.trim() && confirmed.value && !busy.value)
const date = (value: string) => new Date(value).toLocaleString('zh-CN')
async function load() {
  const request = ++sequence; const project = props.projectId
  batchId.value = ''; preview.value = undefined; confirmed.value = false; reason.value = ''; error.value = ''; busy.value = true
  try {
    const [newBatches, newSets] = await Promise.all([listRecycleArchiveBatches(project, props.token), listArchiveRecycleSets(project, props.token)])
    if (request !== sequence) return
    batches.value = newBatches; sets.value = newSets
  } catch { if (request === sequence) error.value = '未能读取项目存档批次，请稍后重试。' }
  finally { if (request === sequence) busy.value = false }
}
async function inspect() {
  const request = ++sequence; const selected = batchId.value; const project = props.projectId
  preview.value = undefined; confirmed.value = false; reason.value = ''; error.value = ''; if (!selected) return
  busy.value = true
  try { const result = await previewRecycleArchive(project, selected, props.token); if (request === sequence) preview.value = result }
  catch (failure) { if (request === sequence) error.value = failure instanceof Error ? failure.message : '预览失败，请刷新后重试。' }
  finally { if (request === sequence) busy.value = false }
}
async function recycle() {
  if (!canRecycle.value || !preview.value) return
  busy.value = true; error.value = ''
  try {
    await recycleArchiveSet(props.projectId, preview.value.batchId, preview.value.scopeHash, reason.value.trim(), props.token)
    ElMessage.success('本批图档已成套移入回收站，30天内可成套恢复')
    await props.onReload(); await load()
  } catch (failure) { error.value = failure instanceof Error ? failure.message : '整批回收未完成，请刷新后核对。'; preview.value = undefined; confirmed.value = false }
  finally { busy.value = false }
}
async function restore(set: ArchiveRecycleSet) {
  try { await ElMessageBox.confirm(`确认成套恢复这批 ${set.members.length} 份图档？项目中其他已有图档和人工BOM保持不变。`, '恢复存档批次', { type: 'warning', confirmButtonText: '确认恢复', cancelButtonText: '取消' }) }
  catch { return }
  busy.value = true; error.value = ''
  try { await restoreArchiveSet(props.projectId, set.id, props.token); ElMessage.success('本批图档及相应结构已恢复'); await props.onReload(); await load() }
  catch (failure) { error.value = failure instanceof Error ? failure.message : '整批恢复未完成，请刷新后核对。' }
  finally { busy.value = false }
}
watch([open, () => props.projectId], () => { if (open.value) void load(); else sequence++ })
</script>

<template>
  <el-dialog v-model="open" title="按存档批次回收 / 恢复图档" width="min(900px, 94vw)" :close-on-click-modal="!busy" :close-on-press-escape="!busy" :show-close="!busy">
    <p>当前项目：<strong>{{ batches[0]?.projectCode || projectId }}</strong></p>
    <p>仅处理本批首次存档、尚未发布且未被范围外图档引用的文件。项目原有图档、其他版本和电脑原图保留。</p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <label>选择存档批次（最近100批）</label>
    <select v-model="batchId" aria-label="选择存档批次" :disabled="busy" @change="inspect" style="width: 100%; min-height: 36px; font: inherit">
      <option value="" disabled>请选择需核对的存档批次</option>
      <option v-for="batch in batches" :key="batch.batchId" :value="batch.batchId">{{ date(batch.committedAt) }} · {{ displayUserName(batch.actor) }} · {{ batch.fileCount }}份图档</option>
    </select>
    <button type="button" class="pdm-secondary-action" :disabled="busy || !batchId" @click="inspect">重新核对回收范围</button>
    <p v-if="!busy && !batches.length">当前项目没有可核对的已成功提交批次。</p>
    <div v-if="preview">
      <p><strong>本次回收 {{ preview.members.length }} 份；保留 {{ preview.retainedFiles.length }} 份。</strong></p>
      <el-alert v-for="blocker in preview.blockers" :key="blocker" :title="blocker" type="warning" :closable="false" />
      <el-collapse>
        <el-collapse-item title="查看回收图档清单" name="members"><div class="pdm-archive-recycle-list"><p v-for="member in preview.members" :key="member.document.id">{{ member.document.fileName }}</p></div></el-collapse-item>
        <el-collapse-item v-if="preview.retainedFiles.length" title="查看保留图档及原因" name="retained"><div class="pdm-archive-recycle-list"><p v-for="item in preview.retainedFiles" :key="item">{{ item }}</p></div></el-collapse-item>
      </el-collapse>
      <el-input v-model="reason" aria-label="回收原因" type="textarea" :rows="2" maxlength="500" show-word-limit placeholder="请填写回收原因" :disabled="busy" />
      <el-checkbox v-model="confirmed" :disabled="busy || !preview.canRecycle">已核对当前项目、存档批次和回收清单</el-checkbox>
      <div><button type="button" class="pdm-danger-action" :disabled="!canRecycle" @click="recycle">整批移入回收站</button></div>
    </div>
    <el-divider>已回收批次</el-divider>
    <p v-if="!sets.length">暂无回收记录。回收后30天内可成套恢复，不提供永久销毁。</p>
    <div v-for="set in sets" :key="set.id" class="pdm-archive-recycle-entry">
      <span>{{ date(set.createdAt) }} · {{ set.members.length }}份 · {{ set.reason }}</span>
      <span v-if="set.restoredAt">已恢复</span>
      <span v-else-if="new Date(set.expiresAt).getTime() <= Date.now()">已超过恢复期限</span>
      <button v-else type="button" class="pdm-secondary-action" :disabled="busy" @click="restore(set)">成套恢复</button>
    </div>
    <template #footer><button type="button" class="pdm-secondary-action" :disabled="busy" @click="open = false">关闭</button></template>
  </el-dialog>
</template>

<style scoped>
.pdm-archive-recycle-list { max-height: 18rem; overflow: auto; }
.pdm-archive-recycle-entry { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 8px 0; }
</style>
