<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import type { PdmSystemSettings } from '../types'

const props = defineProps<{
  settings: PdmSystemSettings
  administrator: boolean
  pending: boolean
  onSaveSettings: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
}>()
const groups = [
  { name: '人员与日期', fields: [['designer', '设计人员'], ['drafter', '设计人员'], ['designedOn', '存档日期'], ['reviewer', '主设审核'], ['reviewedOn', '审核日期'], ['approver', '机械主管批准'], ['approvedOn', '批准日期']] },
  { name: '物料信息', fields: [['name', '物料名称'], ['specification', '型号'], ['material', '材质'], ['surfaceTreatment', '表面处理'], ['heatTreatment', '热处理'], ['wearPart', '易损件'], ['materialCode', '物料编码'], ['weight', '重量']] },
  { name: '发布与项目', fields: [['quantity', '本次下发数量'], ['revision', '版本'], ['projectNumber', '项目号'], ['projectName', '项目名称']] },
] as const
const defaultDrawingLabels: Readonly<Record<string, string>> = {
  designer: '设计',
  drafter: '制图',
  designedOn: '设计日期',
  reviewer: '校对',
  approver: '批准',
  specification: '零件编号',
}
const selected = ref<string[]>([])
const drawingLabels = ref<Record<string, string>>({})
const saving = ref(false)
watch(() => props.settings.pdfTitleBlockFill, value => {
  selected.value = [...(value?.enabledFields ?? [])]
  drawingLabels.value = { ...value?.drawingLabels }
}, { immediate: true, deep: true })
async function save() {
  if (!props.administrator || props.pending || saving.value) return
  const fields = groups.flatMap(group => [...group.fields])
  const labels = Object.fromEntries(Object.entries(drawingLabels.value)
    .map(([key, value]) => [key, value.trim()] as const).filter(([, value]) => value.length > 0))
  const normalize = (value: string) => value.replace(/[\s：:]/g, '').toUpperCase()
  if (Object.values(labels).some(value => !normalize(value))) {
    ElMessage.error('图框标签须包含有效文字')
    return
  }
  const enabledLabels = fields.filter(([key]) => selected.value.includes(key))
    .map(([key, name]) => normalize(labels[key] ?? defaultDrawingLabels[key] ?? name))
  if (new Set(enabledLabels).size !== enabledLabels.length) {
    ElMessage.error('已勾选的字段不能使用相同的图框标签')
    return
  }
  saving.value = true
  try {
    await props.onSaveSettings({ ...props.settings, pdfTitleBlockFill: { enabledFields: [...selected.value], drawingLabels: labels } })
    ElMessage.success('PDF属性自动填写设置已保存，将用于新提交的发布包')
  } catch {
    ElMessage.error('PDF属性自动填写设置保存失败，请检查登录权限和网络连接后重试')
  } finally { saving.value = false }
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel pdf-title-block-settings">
    <header class="pdm-manager-heading"><div><h2>PDF 属性自动填写</h2><p>勾选后自动填写 PDF 原图框的对应位置。找不到图框标签时跳过该字段，继续发布。默认全部关闭。</p></div></header>
    <p>系统字段表示 PLM 数据来源，图框标签用于定位 PDF 中对应的填写位置。两行设计人员分别对应“设计”和“制图”，均取该图纸送审版本的存档工程师姓名，存档日期取该版本的存档日期；合并发布流程的主设审核、机械主管批准取本发布包实际审核、批准人员及通过日期，提交时允许待签字，最终批准后填写并冻结。历史在途包沿用原图纸审核记录。物料信息取发布 BOM，数量取本次下发数量，项目与版本取发布记录。这里只填写PDF，不修改原生图档。</p>
    <fieldset class="pdf-field-list" :disabled="!administrator || pending || saving">
      <table class="pdf-mapping-table" aria-label="PDF 图框字段映射">
        <thead><tr><th scope="col">启用</th><th scope="col">系统字段</th><th scope="col">图框标签</th></tr></thead>
        <tbody v-for="group in groups" :key="group.name">
          <tr class="pdf-field-group"><th colspan="3" scope="rowgroup">{{ group.name }}</th></tr>
          <tr v-for="[key, label] in group.fields" :key="key">
            <td><input v-model="selected" type="checkbox" :value="key" :aria-label="`自动填写${label}${key === 'drafter' ? '（制图）' : ''}`"></td>
            <th scope="row">{{ label }}</th>
            <td>
              <input v-model="drawingLabels[key]" type="text" maxlength="60"
                :placeholder="defaultDrawingLabels[key] ?? label" :aria-label="`${label}${key === 'drafter' ? '（制图）' : ''}对应的图框标签`">
            </td>
          </tr>
        </tbody>
      </table>
    </fieldset>
    <p class="pdm-inline-info">配置随发布包提交审批时冻结。修改仅影响新提交的发布包，历史发布包重转沿用原配置和数据。</p>
    <p v-if="!administrator">需要「编号与存储」管理权限才能修改此设置。</p>
    <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="!administrator || pending || saving" @click="save">{{ saving ? '正在保存…' : '保存 PDF 填写设置' }}</button></div>
  </section>
</template>

<style scoped>
.pdf-title-block-settings { overflow-x: hidden; overflow-y: auto; }
.pdf-field-list { flex: 0 0 auto; min-width: 0; max-width: 620px; margin: 8px 0; padding: 0; border: 0; }
.pdf-mapping-table { width: 100%; border-collapse: collapse; table-layout: fixed; }
.pdf-mapping-table th, .pdf-mapping-table td { text-align: center; vertical-align: middle; padding: 3px 8px; border-bottom: 1px solid var(--pdm-border); }
.pdf-mapping-table thead, .pdf-field-group { background: var(--pdm-table-header); }
.pdf-mapping-table th[scope="row"] { font-weight: 400; }
.pdf-mapping-table thead th:first-child { width: 64px; }
.pdf-mapping-table thead th:nth-child(2) { width: 34%; }
@layer pdm-button-sizing {
  :root body .pdf-mapping-table td input[type="text"] {
    width: 100%;
    max-width: 280px;
    padding-inline: 4px;
    text-align: center;
    border: 0 !important;
    border-radius: 0 !important;
    box-shadow: none !important;
    background: transparent;
  }
}
.pdf-mapping-table td:focus-within { box-shadow: inset 0 -2px var(--pdm-theme-accent); }
.pdf-title-block-settings p { line-height: 1.7; }
</style>
