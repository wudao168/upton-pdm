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
  { name: '人员与日期', fields: [['designer', '设计人'], ['drafter', '制图人'], ['designedOn', '设计日期'], ['reviewer', '审核人'], ['reviewedOn', '审核日期'], ['approver', '批准人'], ['approvedOn', '批准日期']] },
  { name: '物料信息', fields: [['name', '物料名称'], ['specification', '型号'], ['material', '材质'], ['surfaceTreatment', '表面处理'], ['heatTreatment', '热处理'], ['wearPart', '易损件'], ['materialCode', '物料编码'], ['weight', '重量']] },
  { name: '发布与项目', fields: [['quantity', '本次下发数量'], ['revision', '版本'], ['projectNumber', '项目号'], ['projectName', '项目名称']] },
] as const
const selected = ref<string[]>([])
const saving = ref(false)
watch(() => props.settings.pdfTitleBlockFill, value => { selected.value = [...(value?.enabledFields ?? [])] }, { immediate: true, deep: true })
async function save() {
  if (!props.administrator || saving.value) return
  saving.value = true
  try {
    await props.onSaveSettings({ ...props.settings, pdfTitleBlockFill: { enabledFields: [...selected.value] } })
    ElMessage.success('PDF属性自动填写设置已保存，将用于新提交的发布包')
  } catch {
    ElMessage.error('PDF属性自动填写设置保存失败，请检查登录权限和网络连接后重试')
  } finally { saving.value = false }
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel pdf-title-block-settings">
    <header class="pdm-manager-heading"><div><h2>PDF 属性自动填写</h2><p>勾选后自动填写 PDF 原图框的对应位置。默认全部关闭，未勾选的属性保留原内容。</p></div></header>
    <p>设计信息取送审图纸属性，制图人默认与设计人相同；型号填写至“零件编号”格，数量只填写本次下发数值。审核、批准信息取实际审图记录。已启用属性缺失或与冻结 BOM 冲突时，将提示处理。</p>
    <div class="pdf-field-groups">
      <fieldset v-for="group in groups" :key="group.name" :disabled="!administrator || pending || saving">
        <legend>{{ group.name }}</legend>
        <div class="pdf-field-grid">
          <label v-for="[key, label] in group.fields" :key="key" class="pdm-checkbox-field">
            <input v-model="selected" type="checkbox" :value="key" :aria-label="`自动填写${label}`">{{ label }}
          </label>
        </div>
      </fieldset>
    </div>
    <p class="pdm-inline-info">配置随发布包提交审批时冻结。修改仅影响新提交的发布包，历史发布包重转沿用原配置和数据。</p>
    <p v-if="!administrator">只有管理员可以修改此设置。</p>
    <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="!administrator || pending || saving" @click="save">{{ saving ? '正在保存…' : '保存 PDF 填写设置' }}</button></div>
  </section>
</template>

<style scoped>
.pdf-title-block-settings { overflow-x: hidden; overflow-y: auto; }
.pdf-field-groups { flex: 0 0 auto; min-width: 0; }
fieldset { flex: 0 0 auto; border: 1px solid var(--pdm-border-color, #dce3ec); border-radius: 8px; margin: 16px 0; padding: 14px 16px; }
legend { padding: 0 6px; font-weight: 600; }
.pdf-field-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 14px 20px; }
.pdf-field-grid .pdm-checkbox-field { grid-column: auto; min-height: 28px; }
.pdf-title-block-settings p { line-height: 1.7; }
</style>
