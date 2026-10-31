<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { readProjectNameplate, saveProjectNameplate, confirmProjectNameplate, PdmApiError } from '../api'
import { ElMessage } from '../statusMessage'
import type { ProjectSummary } from '../types'
import { nameplateStatus, type ProjectNameplateView } from '../projectNameplate'
import NameplatePreview from './NameplatePreview.vue'
import { useUserDisplayName } from '../userDisplay'

const props = defineProps<{ project: ProjectSummary; token: string; modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const view = ref<ProjectNameplateView | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const editing = ref(false)
const displayName = useUserDisplayName()
const draft = reactive({ voltage: '', frequency: '', current: '', length: '', width: '', height: '', weight: '', airPressure: '' })
let initialDraft = { ...draft }
const disabled = computed(() => !view.value?.canEdit || !view.value.nameplate.enabled || saving.value)
const executionDisabled = computed(() => disabled.value || !view.value?.canEditExecution)
const hardwareDisabled = computed(() => disabled.value || !view.value?.canEditHardware)
const status = computed(() => view.value ? nameplateStatus(view.value.nameplate) : null)
const executionComplete = computed(() => !!status.value?.executionComplete)
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
  draft.airPressure = plate.airPressure.trim().replace(/\s*MPa$/i, '') || (result.canEditExecution ? '0.5~0.7' : '')
  initialDraft = { ...draft }
}
function startEditing() {
  if (view.value?.canEditExecution && !view.value.nameplate.factoryDate) {
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
  catch (e) { if (request === generation) error.value = e instanceof PdmApiError ? e.message : '铭牌读取失败，请检查网络后重试。' }
  finally { if (request === generation) loading.value = false }
}
watch(() => [props.modelValue, props.project.id, props.token], () => {
  if (props.modelValue) void load()
  else ++generation
}, { immediate: true })
async function save() {
  if (!view.value || saving.value) return false
  if (Object.entries(draft).filter(([key]) => key !== 'airPressure').some(([, value]) => value !== '' && (!Number.isFinite(Number(value)) || Number(value) < 0))) { ElMessage.error('请填写有效的非负数字'); return }
  const plate = { ...view.value.nameplate }
  if (view.value.canEditExecution) plate.airPressure = draft.airPressure.trim() ? `${draft.airPressure.trim().replace(/\s*MPa$/i, '')} MPa` : ''
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
  try { setView(await saveProjectNameplate(plate, props.token)); editing.value = false; ElMessage.success('铭牌填写内容已保存'); return true }
  catch (e) { ElMessage.error(e instanceof PdmApiError ? e.message : '铭牌保存失败，请检查网络后重试。'); return false }
  finally { saving.value = false }
}
async function confirm() {
  if (!view.value?.canConfirm || saving.value) return
  if (editing.value && !await save()) return
  saving.value = true
  try { setView(await confirmProjectNameplate(view.value!.nameplate, props.token)); editing.value = false; ElMessage.success('铭牌已确认发布，已转入制造中心。') }
  catch (e) { ElMessage.error(e instanceof PdmApiError ? e.message : '铭牌确认发布失败，请刷新后重试。') }
  finally { saving.value = false }
}
</script>

<template>
  <el-dialog v-model="visible" :title="`铭牌 · ${project.code}`" width="620px" append-to-body :close-on-click-modal="false" :close-on-press-escape="!saving" :show-close="!saving">
    <p v-if="loading" role="status">正在读取铭牌…</p>
    <div v-else-if="error" role="alert">{{ error }} <el-button @click="load">重新读取</el-button></div>
    <template v-else-if="view">
      <div class="nameplate-toolbar"><el-checkbox v-if="editing" v-model="view.nameplate.enabled" :disabled="!view.canEdit || saving" aria-label="启用铭牌">启用铭牌</el-checkbox><span v-else role="status" aria-label="铭牌状态" :class="{ 'is-published': status?.published }">{{ status?.published ? '铭牌已发布' : view.nameplate.enabled ? `${status?.label} · ${view.canEdit ? '可编辑' : '仅查看'}` : '铭牌未启用' }}</span></div>
      <div class="nameplate-assignments" aria-label="铭牌填写分工">
        <p><strong>执行工程师：</strong>设备尺寸、重量、气源压力、出厂月份。<span>{{ executionComplete ? '已填写' : '待填写完整' }}</span><span v-if="view.nameplate.executionSavedBy"> · {{ displayName(view.nameplate.executionSavedBy) }}</span></p>
        <p><strong>硬件工程师：</strong>电压、频率、电流，完成后确认发布。<span>{{ status?.published ? '已填写，已确认发布' : status?.hardwareComplete ? '已填写，待确认发布' : '待填写完整' }}</span></p>
        <p v-if="view.nameplate.releasePackageId">发布包：{{ view.nameplate.releasePackageNumber }} · 硬件确认：{{ displayName(view.nameplate.confirmedBy || '') }}</p>
      </div>
      <NameplatePreview :name="view.name" :model="view.model" :serial-numbers="view.serialNumbers" :power-supply="view.nameplate.powerSupply" :rated-power="ratedPower" :dimensions="view.nameplate.dimensions" :air-pressure="view.nameplate.airPressure" :weight="view.nameplate.weight" :factory-date="view.nameplate.factoryDate">
        <template v-if="editing" #powerSupply><div class="nameplate-numbers nameplate-power-inputs"><label><input :value="draft.voltage" @input="draft.voltage = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="电压" :disabled="hardwareDisabled"><span>V</span></label><label><input :value="draft.frequency" @input="draft.frequency = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="频率" :disabled="hardwareDisabled"><span>Hz</span></label><b>/</b><label><input :value="draft.current" @input="draft.current = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="电流" :disabled="hardwareDisabled"><span>A</span></label></div></template>
        <template v-if="editing" #airPressure><div class="nameplate-numbers"><label><el-input v-model="draft.airPressure" aria-label="气压" maxlength="96" :disabled="executionDisabled" /><span>MPa</span></label></div></template>
        <template v-if="editing" #dimensions><div class="nameplate-numbers nameplate-dimensions"><template v-for="(field, index) in dimensionFields" :key="field.key"><b v-if="index">x</b><label><input :value="draft[field.key]" @input="draft[field.key] = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" :aria-label="field.label" :disabled="executionDisabled"><span>mm</span></label></template></div></template>
        <template v-if="editing" #weight><div class="nameplate-numbers"><label><input :value="draft.weight" @input="draft.weight = ($event.target as HTMLInputElement).value" type="number" min="0" step="any" aria-label="重量" :disabled="executionDisabled"><span>kg</span></label></div></template>
        <template v-if="editing" #factoryDate><el-date-picker v-model="view.nameplate.factoryDate" type="month" format="YYYY-MM" value-format="YYYY-MM-01" aria-label="出厂日期" :disabled="executionDisabled" :clearable="false" /></template>
      </NameplatePreview>
      <p v-if="status?.published" class="nameplate-hint">铭牌已发布，内容已锁定，制造中心按该版本的铭牌内容及数量执行。</p>
      <p v-else class="nameplate-hint">名称、型号、序列号在发布前随项目资料更新，发布后固定。额定功率按电压 × 电流 ÷ 1000 计算。铭牌采用公司统一制作标准；硬件确认后，内容和数量自动转入制造中心。{{ view.nameplate.enabled ? '请填写铭牌参数及出厂月份。' : '铭牌未启用，可启用后填写参数。' }}</p>
    </template>
    <template #footer><el-button :disabled="saving" @click="visible = false">关闭</el-button><el-button v-if="view?.canEdit && !editing" type="primary" @click="startEditing">编辑铭牌</el-button><el-button v-if="editing" :disabled="saving" @click="load">取消编辑</el-button><el-button v-if="view?.canEdit && editing" type="primary" :loading="saving" @click="save">{{ view.canEditExecution && view.canEditHardware ? '保存铭牌' : view.canEditExecution ? '保存执行信息' : '保存硬件信息' }}</el-button><el-button v-if="view?.canConfirm" type="primary" :loading="saving" :disabled="!executionComplete || !view.nameplate.enabled" @click="confirm">确认并发布</el-button></template>
  </el-dialog>
</template>

<style scoped>
.nameplate-assignments{font-size:12px;line-height:1.6;margin-bottom:12px;color:var(--pdm-muted)}.nameplate-assignments p{margin:4px 0}.nameplate-assignments strong{color:var(--pdm-text)}
.nameplate-toolbar{display:flex;align-items:center;margin-bottom:12px}.nameplate-toolbar .is-published{color:var(--pdm-success,#16803c);font-weight:600}
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
.nameplate-hint{font-size:12px;color:var(--pdm-muted);line-height:1.6}
</style>
