<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { importHistoricalProjects, PdmApiError } from '../api'
import type { HistoricalProjectImportResult } from '../types'

const open = defineModel<boolean>({ required: true })
const props = defineProps<{ companyId: string; companyName: string; token: string }>()
const emit = defineEmits<{ imported: [] }>()
const file = ref<File>()
const result = ref<HistoricalProjectImportResult>()
const pending = ref(false)
const error = ref('')
const fileInput = ref<HTMLInputElement>()
const rootCount = computed(() => result.value?.rows.filter(row => !row.isSubproject).length ?? 0)
let previewController: AbortController | undefined

watch(() => [open.value, props.companyId, props.token] as const, ([visible, company], previous) => {
  previewController?.abort()
  if (visible && previous && previous[0] && company !== previous[1]) open.value = false
  file.value = undefined
  result.value = undefined
  error.value = ''
  if (fileInput.value) fileInput.value.value = ''
})

function selectFile(event: Event) {
  file.value = (event.target as HTMLInputElement).files?.[0]
  result.value = undefined
  error.value = ''
}

async function run(preview: boolean) {
  if (pending.value || !file.value || !props.companyId || (!preview && (!result.value || result.value.created))) return
  const company = props.companyId
  const token = props.token
  pending.value = true
  error.value = ''
  if (preview) result.value = undefined
  const controller = preview ? new AbortController() : undefined
  previewController = controller
  try {
    const response = await importHistoricalProjects(company, file.value, preview, token, controller?.signal)
    if (company !== props.companyId || token !== props.token || !open.value || controller?.signal.aborted) return
    result.value = response
    if (response.created) emit('imported')
  } catch (cause) {
    if (controller?.signal.aborted || company !== props.companyId || token !== props.token || !open.value) return
    result.value = undefined
    error.value = cause instanceof PdmApiError && cause.status > 0 && cause.status < 500
      ? cause.message
      : preview ? '清单预检失败，请确认服务可用后重试。' : '未能确认导入结果，请先刷新立项信息并核对项目号。'
  } finally {
    previewController = undefined
    pending.value = false
  }
}
</script>

<template>
  <el-dialog v-model="open" :title="`导入历史项目 · ${companyName}`" width="1060px"
    :close-on-click-modal="!pending" :close-on-press-escape="!pending" :show-close="!pending">
    <div class="historical-project-import">
      <div class="historical-project-import__file">
        <button type="button" class="pdm-file-picker-button" :disabled="pending" @click="fileInput?.click()">选择文件</button>
        <input ref="fileInput" type="file" accept=".xlsx" aria-label="历史项目 Excel" hidden :disabled="pending" @change="selectFile">
        <span>{{ file?.name || '请选择 Excel（.xlsx）清单' }}</span>
      </div>
      <p v-if="!result">请把完整项目树放在同一清单。支持旧清单 B 至 J 列，或使用项目号、项目名称、设备型号、序列号、客户名称作为必填列标题。自定义编号可用「上级项目号」列指定层级。</p>
      <p v-if="error" role="alert" class="historical-project-import__error">{{ error }}</p>
      <p v-if="pending" role="status">{{ result ? '正在导入项目…' : '正在预检清单…' }}</p>
      <template v-if="result">
        <p role="status">{{ result.created ? '导入成功' : '预检通过' }}，共 {{ rootCount }} 个主项目、{{ result.rows.length - rootCount }} 个子项目。</p>
        <div class="historical-project-import__table">
          <table aria-label="历史项目清单" data-pdm-min-width="960">
            <thead><tr>
              <th>项目号</th><th>项目名称</th><th>项目别名</th><th>签订日期</th><th>交货日期</th>
              <th>设备型号</th><th>序列号</th><th>客户名称</th><th>原更新时间</th><th>备注</th>
            </tr></thead>
            <tbody>
              <tr v-for="row in result.rows" :key="row.code">
                <td>{{ row.code }}</td><td>{{ row.name }}</td><td>{{ row.projectAlias || '—' }}</td>
                <td>{{ row.signedDate || '—' }}</td><td>{{ row.deliveryDate || '—' }}</td><td>{{ row.deviceModel }}</td>
                <td><div v-for="serial in row.serialNumbers" :key="serial">{{ serial }}</div></td><td>{{ row.customerName }}</td>
                <td>{{ row.historicalUpdatedDate || '—' }}</td><td class="historical-project-import__remarks">{{ row.historicalRemarks || '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </div>
    <template #footer>
      <button type="button" class="pdm-secondary-action" :disabled="pending" @click="open = false">关闭</button>
      <template v-if="!result?.created">
        <button type="button" class="pdm-secondary-action" :disabled="pending || !file" @click="run(true)">预检清单</button>
        <button type="button" class="pdm-primary-action" data-pdm-primary :disabled="pending || !result" @click="run(false)">确认导入</button>
      </template>
    </template>
  </el-dialog>
</template>

<style scoped>
.historical-project-import { display: grid; gap: 12px; padding: 0 16px; }
.historical-project-import p { margin: 0; }
.historical-project-import__file { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; overflow-wrap: anywhere; }
.historical-project-import__table { overflow-x: auto; }
.historical-project-import__table table { width: 100%; min-width: 960px; }
.historical-project-import__table :is(th, td) { text-align: center; vertical-align: middle; padding: 6px; border-bottom: 1px solid var(--pdm-border); overflow-wrap: anywhere; }
.historical-project-import__remarks { white-space: pre-wrap; }
.historical-project-import__error { color: var(--el-color-danger); }
</style>
