<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { computed, reactive, ref } from 'vue'
import { executeU9BomWrite, previewU9BomWrite, queryU9Bom } from '../api'
import type { U9BomComponentInput, U9BomQueryExecution, U9BomReference, U9BomWriteInput, U9BomWriteOperation, U9BomWritePreview } from '../types'

const props = defineProps<{ token: string; organizationCode: string; writeEnabled: boolean }>()
const querying = ref(false)
const previewing = ref(false)
const executing = ref(false)
const execution = ref<U9BomQueryExecution | null>(null)
const editorOpen = ref(false)
const confirmOpen = ref(false)
const preview = ref<U9BomWritePreview | null>(null)
const confirmation = ref('')
const confirmed = ref(false)
const existingComponentSequences = ref(new Set<number>())
const form = reactive({ itemCode: '', bomVersionCode: 'A1', lot: null as number | null, productUomCode: '' })
const editor = reactive<U9BomWriteInput>({
  operation: 0, itemCode: '', bomVersionCode: 'A1', productUomCode: '001', lot: 1,
  effectiveDate: '2000-01-01', disableDate: '9999-12-31', bomSort: 0, bomType: 0,
  projectMapNum: '', explain: '', components: [],
})

const operationNames: Record<U9BomWriteOperation, string> = { 0: '创建', 1: '修改', 2: '删除' }
const editorTitle = computed(() => `${operationNames[editor.operation]}U9C BOM`)
const queryRequestPreview = computed(() => prettyJson(execution.value?.requestPreview ?? ''))
const writeRequestPreview = computed(() => prettyJson(preview.value?.requestPreview ?? ''))
const canExecute = computed(() => !!preview.value && confirmed.value && confirmation.value === preview.value.requiredConfirmation)
const bomRowKey = (row: { itemCode?: string | null; bomVersionCode?: string | null; lot?: number | null }) => `${row.itemCode ?? ''}|${row.bomVersionCode ?? ''}|${row.lot ?? ''}`
const isPdmOwned = (row: U9BomReference) => row.otherId?.startsWith('pdm-bom-') === true
const canModify = (row: U9BomReference) => isPdmOwned(row) && row.bomVersionCode?.toUpperCase() === 'A1'
const isExistingComponent = (row: U9BomComponentInput) => editor.operation === 1 && existingComponentSequences.value.has(row.sequence)

function prettyJson(value: string) {
  if (!value) return ''
  try { return JSON.stringify(JSON.parse(value), null, 2) } catch { return value }
}

function defaultComponent(sequence = 10): U9BomComponentInput {
  return { sequence, itemCode: '', usageQty: 1, issueUomCode: '001', parentQty: 1, isEffective: true, effectiveDate: '2000-01-01', disableDate: '9999-12-31', isDelete: false }
}

function openCreate() {
  Object.assign(editor, {
    operation: 0, itemCode: '', bomVersionCode: 'A1', productUomCode: '001', lot: 1,
    effectiveDate: '2000-01-01', disableDate: '9999-12-31', bomSort: 0, bomType: 0,
    projectMapNum: '', explain: '', components: [defaultComponent()],
  })
  existingComponentSequences.value = new Set()
  preview.value = null
  editorOpen.value = true
}

function loadExisting(row: U9BomReference) {
  if (!canModify(row)) {
    ElMessage.warning(isPdmOwned(row) ? 'PLM只允许维护固定A1版本' : '该BOM不是由PLM受控创建，只允许查询')
    return
  }
  Object.assign(editor, {
    operation: 1,
    itemCode: row.itemCode ?? '', bomVersionCode: 'A1',
    productUomCode: row.productUomCode ?? '001', lot: row.lot ?? 1,
    effectiveDate: row.effectiveDate?.slice(0, 10) ?? '2000-01-01',
    disableDate: row.disableDate?.slice(0, 10) ?? '9999-12-31',
    bomSort: row.bomSort ?? 0, bomType: row.bomType ?? 0,
    projectMapNum: row.projectMapNum ?? '', explain: row.explain ?? '',
    components: row.components.map((component, index) => ({
      sequence: component.sequence ?? (index + 1) * 10, itemCode: component.itemCode ?? '',
      usageQty: component.usageQty ?? 1, issueUomCode: component.issueUomCode ?? '001', parentQty: component.parentQty ?? 1,
      itemVersionCode: component.itemVersionCode ?? '', isEffective: component.isEffective ?? true,
      effectiveDate: component.effectiveDate?.slice(0, 10) ?? '2000-01-01', disableDate: component.disableDate?.slice(0, 10) ?? '9999-12-31',
      remark: component.remark ?? '', componentType: component.componentType ?? 0,
      issueStyle: component.issueStyle ?? 0, supplyStyle: component.supplyStyle ?? 0,
      isPhantomPart: component.isPhantomPart ?? false, isDelete: false,
    })),
  })
  existingComponentSequences.value = new Set(editor.components.map(component => component.sequence))
  preview.value = null
  editorOpen.value = true
}

function addComponent() {
  const sequence = editor.components.reduce((max, row) => Math.max(max, row.sequence), 0) + 10
  editor.components.push(defaultComponent(sequence))
}

function removeComponent(index: number) {
  if (isExistingComponent(editor.components[index])) return
  editor.components.splice(index, 1)
}

async function runQuery() {
  if (!form.itemCode.trim()) return ElMessage.warning('请输入母件料号')
  querying.value = true
  execution.value = null
  try {
    execution.value = await queryU9Bom({
      itemCode: form.itemCode.trim(), bomVersionCode: form.bomVersionCode.trim() || null,
      lot: form.lot, productUomCode: form.productUomCode.trim() || null,
    }, props.token)
    ElMessage.success(`查询完成，返回 ${execution.value.result.boms.length} 张BOM`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C BOM查询失败')
  } finally { querying.value = false }
}

async function buildPreview() {
  if (!editor.itemCode.trim()) return ElMessage.warning('母件料号不能为空')
  editor.bomVersionCode = 'A1'
  previewing.value = true
  try {
    preview.value = await previewU9BomWrite(editor, props.token)
    confirmation.value = ''
    confirmed.value = false
    confirmOpen.value = true
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'BOM请求预览失败')
  } finally { previewing.value = false }
}

async function executeWrite() {
  if (!preview.value || !canExecute.value) return
  executing.value = true
  try {
    const result = await executeU9BomWrite(editor, preview.value.requestSha256, confirmation.value, props.token)
    ElMessage.success(`U9C BOM${operationNames[editor.operation]}成功，自动回查通过`)
    confirmOpen.value = false
    editorOpen.value = false
    form.itemCode = editor.itemCode
    form.bomVersionCode = editor.bomVersionCode
    execution.value = { queryPath: '/webapi/BOM/Query', requestPreview: '', queriedAt: result.executedAt, result: result.verification }
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : `U9C BOM${operationNames[editor.operation]}失败`)
  } finally { executing.value = false }
}
</script>

<template>
  <section class="pdm-panel u9-settings-card u9-bom-query" aria-label="U9C BOM查询与维护">
    <header class="u9-card-heading">
      <div><h2>BOM查询与维护</h2><p>查询U9C BOM；创建和追加修改均先生成完整请求预览，经人工确认后执行并自动回查。</p></div>
      <div class="u9-bom-query__heading-actions">
        <span class="pdm-status" :class="writeEnabled ? 'is-ok' : 'is-muted'">{{ writeEnabled ? '受控写入已启用' : '只读' }}</span>
        <el-button type="primary" :disabled="!writeEnabled" @click="openCreate">新建BOM</el-button>
      </div>
    </header>
    <el-alert :title="writeEnabled ? 'U9C BOM固定使用A1：原有子件只读保留，只能新增项次，禁止修改原行或删除整张BOM。每次执行前都会重新查询，阻止过期预览和并发覆盖。' : '基础设置未启用真实写入，当前只能查询。'" :type="writeEnabled ? 'warning' : 'info'" :closable="false" show-icon />
    <el-form label-position="top" class="u9-settings-form" @submit.prevent="runQuery">
      <div class="u9-bom-query__form">
        <el-form-item label="母件料号" required><el-input v-model="form.itemCode" name="u9BomItemCode" clearable /></el-form-item>
        <el-form-item label="BOM版本"><el-input v-model="form.bomVersionCode" name="u9BomVersionCode" clearable /></el-form-item>
        <el-form-item label="组织编码"><el-input :model-value="organizationCode" name="u9BomOrganizationCode" disabled /></el-form-item>
        <el-form-item label="批量"><el-input-number v-model="form.lot" name="u9BomLot" :min="0" controls-position="right" /></el-form-item>
        <el-form-item label="生产单位编码"><el-input v-model="form.productUomCode" name="u9BomProductUomCode" clearable /></el-form-item>
        <div class="u9-bom-query__action"><el-button type="primary" native-type="submit" :loading="querying">查询U9C</el-button></div>
      </div>
    </el-form>

    <template v-if="execution">
      <div class="u9-bom-query__summary"><span>接口：{{ execution.queryPath }}</span><span>ResCode：{{ execution.result.responseCode }}</span><span>返回：{{ execution.result.boms.length }} 张</span></div>
      <el-collapse v-if="queryRequestPreview" class="u9-bom-query__preview"><el-collapse-item title="查看本次查询请求（不含Token）"><pre>{{ queryRequestPreview }}</pre></el-collapse-item></el-collapse>
      <el-table :data="execution.result.boms" :row-key="bomRowKey" empty-text="U9C未返回符合条件的BOM" class="u9-bom-query__table">
        <el-table-column type="expand" width="42">
          <template #default="{ row }">
            <div class="u9-bom-query__components">
              <h3>子项明细（{{ row.components.length }}）</h3>
              <el-table :data="row.components" size="small" empty-text="该BOM没有返回子项">
                <el-table-column prop="sequence" label="项次" width="70" />
                <el-table-column prop="itemCode" label="子件料号" min-width="130" />
                <el-table-column prop="itemName" label="名称" min-width="130" show-overflow-tooltip />
                <el-table-column prop="itemVersionCode" label="版本" width="80" />
                <el-table-column prop="usageQty" label="用量" width="90" />
                <el-table-column label="发料单位" width="105"><template #default="scope">{{ scope.row.issueUomName || scope.row.issueUomCode || '—' }}</template></el-table-column>
                <el-table-column prop="parentQty" label="母件底数" width="90" />
                <el-table-column prop="remark" label="备注" min-width="150" show-overflow-tooltip />
              </el-table>
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="itemCode" label="母件料号" min-width="135" />
        <el-table-column prop="itemName" label="母件名称" min-width="150" show-overflow-tooltip />
        <el-table-column prop="bomVersionCode" label="BOM版本" width="100" />
        <el-table-column prop="organizationCode" label="组织" width="80" />
        <el-table-column label="生产单位" width="100"><template #default="scope">{{ scope.row.productUomName || scope.row.productUomCode || '—' }}</template></el-table-column>
        <el-table-column prop="lot" label="批量" width="75" />
        <el-table-column label="来源" width="95"><template #default="scope"><el-tag :type="isPdmOwned(scope.row) ? 'success' : 'info'" size="small">{{ isPdmOwned(scope.row) ? 'PLM受控' : 'U9C既有' }}</el-tag></template></el-table-column>
        <el-table-column label="子项数" width="75"><template #default="scope">{{ scope.row.components.length }}</template></el-table-column>
        <el-table-column prop="projectMapNum" label="工程图号" min-width="110" show-overflow-tooltip />
        <el-table-column prop="explain" label="描述" min-width="150" show-overflow-tooltip />
        <el-table-column v-if="writeEnabled" label="操作" width="120" fixed="right">
          <template #default="scope"><el-button link type="primary" :disabled="!canModify(scope.row)" @click="loadExisting(scope.row)">追加修改</el-button></template>
        </el-table-column>
      </el-table>
    </template>

    <el-drawer v-model="editorOpen" :title="editorTitle" size="760px" destroy-on-close>
      <el-alert v-if="editor.operation === 1" title="下表中的U9C既有项只读保留；本次只能添加新项次。PLM正式BOM与U9C历史保留项分别管理，不用升版。" type="warning" :closable="false" show-icon />
      <el-form label-position="top" class="u9-bom-editor">
        <div class="u9-bom-editor__master">
          <el-form-item label="母件料号" required><el-input v-model="editor.itemCode" name="u9BomWriteItemCode" :disabled="editor.operation !== 0" /></el-form-item>
          <el-form-item label="BOM版本（固定）" required><el-input v-model="editor.bomVersionCode" name="u9BomWriteVersionCode" disabled /></el-form-item>
          <el-form-item label="产品单位编码" required><el-input v-model="editor.productUomCode" name="u9BomWriteProductUom" /></el-form-item>
          <el-form-item label="生产批量" required><el-input-number v-model="editor.lot" name="u9BomWriteLot" :min="1" /></el-form-item>
          <el-form-item label="生效日期"><el-date-picker v-model="editor.effectiveDate" value-format="YYYY-MM-DD" /></el-form-item>
          <el-form-item label="失效日期"><el-date-picker v-model="editor.disableDate" value-format="YYYY-MM-DD" /></el-form-item>
          <el-form-item label="工程图号"><el-input v-model="editor.projectMapNum" /></el-form-item>
          <el-form-item label="描述"><el-input v-model="editor.explain" /></el-form-item>
        </div>
        <div class="u9-bom-editor__component-heading"><h3>子件</h3><el-button @click="addComponent">添加子件</el-button></div>
        <el-table :data="editor.components" size="small" class="u9-bom-editor__table">
          <el-table-column label="项次" width="90"><template #default="scope"><el-input-number v-model="scope.row.sequence" :min="1" :controls="false" :disabled="isExistingComponent(scope.row)" /></template></el-table-column>
          <el-table-column label="子件料号" min-width="145"><template #default="scope"><el-input v-model="scope.row.itemCode" :disabled="isExistingComponent(scope.row)" /></template></el-table-column>
          <el-table-column label="用量" width="105"><template #default="scope"><el-input-number v-model="scope.row.usageQty" :min="0.000001" :controls="false" :disabled="isExistingComponent(scope.row)" /></template></el-table-column>
          <el-table-column label="发料单位" width="105"><template #default="scope"><el-input v-model="scope.row.issueUomCode" :disabled="isExistingComponent(scope.row)" /></template></el-table-column>
          <el-table-column label="母件底数" width="105"><template #default="scope"><el-input-number v-model="scope.row.parentQty" :min="0.000001" :controls="false" :disabled="isExistingComponent(scope.row)" /></template></el-table-column>
          <el-table-column label="备注" min-width="130"><template #default="scope"><el-input v-model="scope.row.remark" :disabled="isExistingComponent(scope.row)" /></template></el-table-column>
          <el-table-column label="状态/操作" width="110"><template #default="scope"><el-tag v-if="isExistingComponent(scope.row)" type="info" size="small">U9C既有·保留</el-tag><el-button v-else link type="danger" @click="removeComponent(scope.$index)">移除</el-button></template></el-table-column>
        </el-table>
      </el-form>
      <template #footer><el-button @click="editorOpen = false">取消</el-button><el-button type="primary" :loading="previewing" @click="buildPreview">生成请求预览</el-button></template>
    </el-drawer>

    <el-dialog v-model="confirmOpen" :title="`${editorTitle}确认`" width="760px" append-to-body>
      <el-alert title="系统将在执行前重新查询U9C；若BOM已变化，本次预览自动失效。" type="warning" :closable="false" show-icon />
      <div v-if="preview" class="u9-bom-confirm">
        <p><strong>接口：</strong>{{ preview.path }}</p>
        <p><strong>差异：</strong>本次新增 {{ preview.addedComponentCount }} 项；U9C历史保留 {{ preview.retainedHistoricalComponentCount }} 项</p>
        <p><strong>请求SHA-256：</strong><code>{{ preview.requestSha256 }}</code></p>
        <pre>{{ writeRequestPreview }}</pre>
        <el-checkbox v-model="confirmed">我已核对母件、版本、子件、用量和接口路径</el-checkbox>
        <el-form-item :label="`请输入：${preview.requiredConfirmation}`"><el-input v-model="confirmation" name="u9BomConfirmation" autocomplete="off" /></el-form-item>
      </div>
      <template #footer><el-button @click="confirmOpen = false">取消</el-button><el-button type="primary" :disabled="!canExecute" :loading="executing" @click="executeWrite">确认执行并自动回查</el-button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.u9-bom-query{height:100%;overflow:auto}.u9-bom-query__heading-actions{display:flex;align-items:center;gap:12px}.u9-bom-query__form{display:grid;grid-template-columns:repeat(5,minmax(130px,1fr)) auto;align-items:end;gap:0 14px}.u9-bom-query__action{padding-bottom:18px}.u9-bom-query__summary{display:flex;gap:24px;margin:16px 0 8px;color:#475569}.u9-bom-query__preview pre,.u9-bom-confirm pre{max-height:260px;margin:0;overflow:auto;white-space:pre-wrap;font-size:12px}.u9-bom-query__table{width:100%;margin-top:12px}.u9-bom-query__components{padding:8px 18px 18px}.u9-bom-query__components h3,.u9-bom-editor__component-heading h3{margin:0;font-size:14px}.u9-bom-editor__master{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 16px}.u9-bom-editor__component-heading{display:flex;align-items:center;justify-content:space-between;margin:10px 0}.u9-bom-editor__table :deep(.el-input-number){width:100%}.u9-bom-confirm{display:grid;gap:12px;margin-top:16px}.u9-bom-confirm p{margin:0}.u9-bom-confirm code{font-size:11px;word-break:break-all}.u9-bom-confirm pre{padding:12px;background:#0f172a;color:#e2e8f0;border-radius:6px}@media(max-width:1100px){.u9-bom-query__form,.u9-bom-editor__master{grid-template-columns:repeat(2,minmax(0,1fr))}.u9-bom-query__action{padding-bottom:18px}}
</style>
