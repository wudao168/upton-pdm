<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { readProjectNameplate, saveProjectNameplate } from '../api'
import { ElMessage } from '../statusMessage'
import type { ProjectSummary } from '../types'
import type { ProjectNameplateView } from '../projectNameplate'
import logo from '../assets/upton-logo-white.png'

const props = defineProps<{ project: ProjectSummary; token: string; modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const view = ref<ProjectNameplateView | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const editing = ref(false)
const draft = reactive({ voltage: '', frequency: '', current: '', length: '', width: '', height: '', weight: '', airPressure: '' })
let initialDraft = { ...draft }
const disabled = computed(() => !view.value?.canEdit || !view.value.nameplate.enabled || saving.value)
const ratedPower = computed(() => draft.voltage && draft.current ? `${Number((Number(draft.voltage) * Number(draft.current) / 1000).toFixed(3))} kW` : '—')
const dimensionFields = [{ key: 'length', label: '长度' }, { key: 'width', label: '宽度' }, { key: 'height', label: '高度' }] as const
let generation = 0
function setView(result: ProjectNameplateView) {
  view.value = result
  const plate = result.nameplate
  draft.voltage = plate.powerSupply.match(/(\d+(?:\.\d+)?)\s*V/i)?.[1] ?? ''
  draft.frequency = plate.powerSupply.match(/(\d+(?:\.\d+)?)\s*Hz/i)?.[1] ?? ''
  draft.current = plate.powerSupply.match(/(\d+(?:\.\d+)?)\s*A(?:\b|$)/i)?.[1] ?? ''
  const dimensions = plate.dimensions.match(/^(\d+(?:\.\d+)?)\s*(?:mm)?\s*[x×*]\s*(\d+(?:\.\d+)?)\s*(?:mm)?\s*[x×*]\s*(\d+(?:\.\d+)?)\s*(?:mm)?$/i)
  draft.length = dimensions?.[1] ?? ''
  draft.width = dimensions?.[2] ?? ''
  draft.height = dimensions?.[3] ?? ''
  draft.weight = plate.weight.match(/^(\d+(?:\.\d+)?)\s*(?:kg)?$/i)?.[1] ?? ''
  draft.airPressure = plate.airPressure.trim().replace(/\s*MPa$/i, '') || '0.5~0.7'
  initialDraft = { ...draft }
}
function startEditing() {
  if (view.value && !view.value.nameplate.factoryDate) {
    const now = new Date()
    view.value.nameplate.factoryDate = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-01`
  }
  editing.value = true
}
async function load() {
  const request = ++generation
  editing.value = false
  loading.value = true
  error.value = ''
  view.value = null
  try { const result = await readProjectNameplate(props.project.id, props.token); if (request === generation) setView(result) }
  catch (e) { if (request === generation) error.value = e instanceof Error ? e.message : '铭牌读取失败' }
  finally { if (request === generation) loading.value = false }
}
watch(() => [props.modelValue, props.project.id, props.token], () => {
  if (props.modelValue) void load()
  else ++generation
}, { immediate: true })
async function save() {
  if (!view.value || saving.value) return
  if (Object.entries(draft).filter(([key]) => key !== 'airPressure').some(([, value]) => value !== '' && (!Number.isFinite(Number(value)) || Number(value) < 0))) { ElMessage.error('请填写有效的非负数字'); return }
  const plate = { ...view.value.nameplate }
  plate.airPressure = draft.airPressure.trim() ? `${draft.airPressure.trim().replace(/\s*MPa$/i, '')} MPa` : ''
  if (draft.voltage !== initialDraft.voltage || draft.frequency !== initialDraft.frequency || draft.current !== initialDraft.current) {
    if (!!draft.voltage !== !!draft.current || (draft.frequency && !draft.voltage)) { ElMessage.error('请同时填写电压和电流'); return }
    plate.powerSupply = draft.voltage ? `${draft.voltage}V${draft.frequency ? ` ${draft.frequency}Hz` : ''} / ${draft.current}A` : ''
  }
  if (dimensionFields.some(({ key }) => draft[key] !== initialDraft[key])) {
    if (dimensionFields.some(({ key }) => draft[key]) && dimensionFields.some(({ key }) => !draft[key])) { ElMessage.error('请完整填写长、宽、高'); return }
    plate.dimensions = draft.length ? `${draft.length}mm x ${draft.width}mm x ${draft.height}mm` : ''
  }
  if (draft.weight !== initialDraft.weight) plate.weight = draft.weight ? `${draft.weight} kg` : ''
  saving.value = true
  try { setView(await saveProjectNameplate(plate, props.token)); editing.value = false; ElMessage.success('铭牌已保存') }
  catch (e) { ElMessage.error(e instanceof Error ? e.message : '铭牌保存失败') }
  finally { saving.value = false }
}
</script>

<template>
  <el-dialog v-model="visible" :title="`铭牌 · ${project.code}`" width="900px" append-to-body :close-on-click-modal="false" :close-on-press-escape="!saving" :show-close="!saving">
    <p v-if="loading" role="status">正在读取铭牌…</p>
    <div v-else-if="error" role="alert">{{ error }} <el-button @click="load">重新读取</el-button></div>
    <template v-else-if="view">
      <div class="nameplate-toolbar"><el-checkbox v-if="editing" v-model="view.nameplate.enabled" :disabled="!view.canEdit || saving" aria-label="启用铭牌">启用铭牌</el-checkbox><span v-else>{{ view.nameplate.enabled ? '铭牌已启用' : '铭牌未启用' }}</span></div>
      <div class="nameplate-preview-size"><div class="nameplate-preview" aria-label="铭牌信息">
        <header class="nameplate-brand"><img :src="logo" alt="UPTON"><div><h2>昆山阿普顿自动化系统有限公司</h2><p>UPTON AUTOMATION SYSTEMS Co., Ltd.</p></div></header>
        <table class="nameplate-table"><colgroup><col class="nameplate-label"><col><col class="nameplate-label"><col></colgroup><tbody>
          <tr><th>电源<span>Power Supply</span></th><td><span v-if="!editing" class="nameplate-value">{{ view.nameplate.powerSupply || '—' }}</span><div v-else class="nameplate-numbers nameplate-power-inputs"><label><input :value="draft.voltage" @input="draft.voltage = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="电压" :disabled="disabled"><span>V</span></label><label><input :value="draft.frequency" @input="draft.frequency = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="频率" :disabled="disabled"><span>Hz</span></label><b>/</b><label><input :value="draft.current" @input="draft.current = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="电流" :disabled="disabled"><span>A</span></label></div></td><th>额定功率<span>Rated Power</span></th><td class="nameplate-power" aria-label="额定功率">{{ ratedPower }}</td></tr>
          <tr><th>气源压力<span>Air Pressure</span></th><td><span v-if="!editing" class="nameplate-value">{{ view.nameplate.airPressure || '—' }}</span><div v-else class="nameplate-numbers"><label><el-input v-model="draft.airPressure" aria-label="气压" maxlength="96" :disabled="disabled" /><span>MPa</span></label></div></td><th>外形尺寸<span>Overall Dimensions</span></th><td><span v-if="!editing" class="nameplate-value">{{ view.nameplate.dimensions || '—' }}</span><div v-else class="nameplate-numbers nameplate-dimensions"><template v-for="(field, index) in dimensionFields" :key="field.key"><b v-if="index">x</b><label><input :value="draft[field.key]" @input="draft[field.key] = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" :aria-label="field.label" :disabled="disabled"><span>mm</span></label></template></div></td></tr>
          <tr><th>重量<span>Weight</span></th><td><span v-if="!editing" class="nameplate-value">{{ view.nameplate.weight || '—' }}</span><div v-else class="nameplate-numbers"><label><input :value="draft.weight" @input="draft.weight = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="重量" :disabled="disabled"><span>kg</span></label></div></td><th>出厂日期<span>Mfg. Date</span></th><td><span v-if="!editing" class="nameplate-value">{{ view.nameplate.factoryDate?.slice(0, 7) || '—' }}</span><el-date-picker v-else v-model="view.nameplate.factoryDate" type="month" format="YYYY-MM" value-format="YYYY-MM-01" aria-label="出厂日期" :disabled="disabled" :clearable="false" /></td></tr>
          <tr><th>型号<span>Model</span></th><td colspan="3">{{ view.model || '—' }}</td></tr>
          <tr><th>序列号<span>S/N</span></th><td colspan="3">{{ view.serialNumbers.join('、') || '—' }}</td></tr>
          <tr><th>设备名称<span>Equipment Name</span></th><td colspan="3">{{ view.name || '—' }}</td></tr>
        </tbody></table>
        <footer>MADE IN CHINA</footer>
      </div></div>
      <p class="nameplate-hint">名称、型号、序列号随项目资料自动更新。额定功率按电压 × 电流 ÷ 1000 计算。{{ view.nameplate.enabled ? '请填写铭牌参数及出厂月份。' : '铭牌未启用，可启用后填写参数。' }}</p>
    </template>
    <template #footer><el-button :disabled="saving" @click="visible = false">关闭</el-button><el-button v-if="view?.canEdit && !editing" type="primary" @click="startEditing">编辑铭牌</el-button><el-button v-if="editing" :disabled="saving" @click="load">取消编辑</el-button><el-button v-if="view?.canEdit && editing" type="primary" :loading="saving" @click="save">保存铭牌</el-button></template>
  </el-dialog>
</template>

<style scoped>
.nameplate-preview{user-select:text}
.nameplate-value{display:block;text-align:center}
.nameplate-toolbar{display:flex;align-items:center;margin-bottom:12px}
.nameplate-preview-size{width:calc(100% * 2 / 3);margin-inline:auto}
.nameplate-preview{zoom:calc(2 / 3)}
.nameplate-preview{border:2px solid #333;border-radius:22px;padding:18px;background:#fff;color:#222;overflow-x:auto}
.nameplate-brand{position:relative;display:flex;align-items:center;min-height:128px;margin-bottom:14px;min-width:620px}
.nameplate-brand>div{position:absolute;left:50%;transform:translateX(-50%);text-align:center;white-space:nowrap}
.nameplate-brand img{width:160px;height:102.4px;flex:0 0 160px;object-fit:contain;filter:brightness(0)}
.nameplate-brand h2{margin:0;font-size:22px;letter-spacing:1px}
.nameplate-brand p{margin:4px 0 0;font-size:16px;letter-spacing:1px}
.nameplate-table{width:100%;min-width:620px;border-collapse:collapse;table-layout:fixed;border:2px solid #333;font-size:16px}
.nameplate-label{width:18%}
.nameplate-table th,.nameplate-table td{border:2px solid #333;padding:8px;font-weight:500;overflow-wrap:anywhere}
.nameplate-table th{text-align:center;font-weight:600;font-size:17px}
.nameplate-table td[colspan="3"]{text-align:center}
.nameplate-table th span{display:block;font-size:12px;letter-spacing:.5px;margin-top:3px}
.nameplate-table :deep(.el-input__wrapper){padding:1px 5px}
.nameplate-table :deep(.el-date-editor){width:100%;min-width:0}
.nameplate-power{text-align:center;font-size:22px}
.nameplate-numbers{display:flex;align-items:center;gap:4px;min-width:0}
.nameplate-numbers label{display:flex;align-items:center;flex:1;min-width:0;gap:3px;font-size:12px}
.nameplate-numbers label>span{flex-shrink:0;white-space:nowrap}
.nameplate-numbers input{box-sizing:border-box;width:100%;min-width:0;height:30px;border:1px solid var(--pdm-border);border-radius:4px;padding:2px 4px;font:inherit;font-size:14px;appearance:textfield;-moz-appearance:textfield}
.nameplate-numbers input::-webkit-inner-spin-button,.nameplate-numbers input::-webkit-outer-spin-button{-webkit-appearance:none;margin:0}
.nameplate-numbers input:disabled{background:var(--el-disabled-bg-color);color:var(--el-disabled-text-color)}
.nameplate-numbers input:focus{outline:1px solid var(--pdm-blue)}
.nameplate-power-inputs input,.nameplate-dimensions input{text-align:center}
.nameplate-dimensions{gap:2px}
.nameplate-dimensions input{min-width:52px;padding:2px;font-size:11px}
.nameplate-dimensions label{gap:1px;font-size:10px}
.nameplate-preview footer{text-align:right;min-width:620px;margin-top:14px;font-size:18px;letter-spacing:1px}
.nameplate-hint{font-size:12px;color:var(--pdm-muted);line-height:1.6}
</style>
