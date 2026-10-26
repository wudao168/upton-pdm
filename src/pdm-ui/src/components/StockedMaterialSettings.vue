<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { listMaterialStockPlans, readStockedMaterialSettings, refreshStockedMaterials, saveStockedMaterialSettings } from '../api'
import { refreshMaterialStockPlans } from '../materialStockPlans'
import { ElMessage } from '../statusMessage'
import { stockTime, type StockedMaterialSettingsView } from '../stockedMaterials'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ token: string }>()
const value = ref<StockedMaterialSettingsView | null>(null), loading = ref(false), saving = ref(false), error = ref('')
const synchronizing = ref(false)
const savedSettings = ref('')
const hasUnsavedChanges = computed(() => value.value && JSON.stringify(value.value.settings) !== savedSettings.value)
const defaultNames = computed(() => value.value?.users.filter(user => user.isDefault).map(user => user.displayName).join('、') || '未找到有效人员，请在用户设置中分配“标准化助理”角色，或改为指定人员')
const hours = computed({ get: () => (value.value?.settings.syncIntervalMinutes ?? 240) / 60,
  set: number => { if (value.value) value.value.settings.syncIntervalMinutes = Math.round(number * 60) } })
async function load() {
  loading.value = true; error.value = ''
  try { value.value = await readStockedMaterialSettings(props.token); savedSettings.value = JSON.stringify(value.value.settings) }
  catch (reason) { value.value = null; error.value = userErrorMessage(reason instanceof Error ? reason.message : '常备件设置加载失败，请重试。') }
  finally { loading.value = false }
}
async function save() {
  if (!value.value || saving.value || synchronizing.value || loading.value) return
  saving.value = true; error.value = ''
  try { value.value = await saveStockedMaterialSettings(value.value.settings, value.value.rowVersion, props.token); savedSettings.value = JSON.stringify(value.value.settings); ElMessage.success('常备件同步与提醒设置已保存') }
  catch (reason) { error.value = userErrorMessage(reason instanceof Error ? reason.message : '保存失败，请刷新后重试。') }
  finally { saving.value = false }
}
async function synchronize() {
  if (!value.value || synchronizing.value || saving.value || loading.value) return
  synchronizing.value = true; error.value = ''
  let failure = ''
  try { await refreshStockedMaterials(props.token) }
  catch (reason) { failure = userErrorMessage(reason instanceof Error ? reason.message : '立即同步未完成，请检查U9C连接后重试。') }
  await refreshMaterialStockPlans(() => listMaterialStockPlans(props.token))
  try {
    const latest = await readStockedMaterialSettings(props.token)
    if (JSON.stringify(latest.settings) === savedSettings.value) value.value = { ...latest, settings: value.value.settings }
    else {
      value.value = { ...value.value, lastSuccessAt: latest.lastSuccessAt, lastError: latest.lastError }
      failure ||= '同步结果已更新，但设置已被其他人修改，请刷新设置后重新编辑。'
    }
    error.value = failure || latest.lastError || ''
    if (!error.value) ElMessage.success('常备件立即同步已完成，库存规划及现存量已回读')
  } catch (reason) { error.value = failure || userErrorMessage(reason instanceof Error ? reason.message : '同步结果读取失败，请刷新设置核对。') }
  finally { synchronizing.value = false }
}
watch(() => props.token, () => void load(), { immediate: true })
</script>

<template>
  <section v-loading="loading" class="stocked-settings pdm-panel" aria-label="常备件设置">
    <header><strong>常备件同步与提醒</strong><div class="stocked-settings__actions"><el-button :loading="loading" :disabled="saving || synchronizing" @click="load">刷新设置</el-button><el-button type="primary" :loading="synchronizing" :disabled="loading || saving || !value" @click="synchronize">{{ synchronizing ? '同步中…' : '立即同步' }}</el-button></div></header>
    <p v-if="error" class="stocked-settings__error" role="alert">{{ error }}</p>
    <p v-if="synchronizing" class="field-help" role="status">正在从U9C回读安全库存量、再订货量及现存量，请稍候…</p>
    <el-form v-if="value" label-position="top" :disabled="synchronizing" @submit.prevent="save">
      <el-form-item label="U9C自动回读间隔（小时）"><el-input-number v-model="hours" aria-label="U9C自动回读间隔（小时）" :min="0.25" :max="24" :step="0.25" :precision="2" /></el-form-item>
      <p class="field-help">默认每4小时从当前公司对应的U9C企业与组织发现全部常备件，补齐PLM料品主档并收录标准库、常备库，同时回读安全库存量、再订货量及现存量。判定条件：料品有效且安全库存量大于0；已有料号及人工分类保留，新增目录按U9C原分类名称和层级收录。工具刀具、包装材料、非金属仅用于常备件管理；标准机加件按标准件管理。</p>
      <el-form-item label="低库存提醒"><el-switch v-model="value.settings.notifyLowStock" aria-label="启用低库存提醒" active-text="启用站内消息" /></el-form-item>
      <el-form-item label="提醒接收人">
        <el-radio-group v-model="value.settings.useStandardAssistant"><el-radio :value="true">标准化助理（跟随角色人员）</el-radio><el-radio :value="false">指定人员（可多选）</el-radio></el-radio-group>
        <p v-if="value.settings.useStandardAssistant" class="field-help">{{ defaultNames }}</p>
        <el-select v-else v-model="value.settings.recipients" multiple filterable clearable placeholder="选择接收人员" aria-label="低库存提醒接收人"><el-option v-for="user in value.users" :key="user.username" :value="user.username" :label="`${user.displayName}（${user.username}）`" /></el-select>
      </el-form-item>
      <p class="field-help">草稿不计入正式常备件，不参与BOM、备料、导出标识和低库存提醒。其他业务模块只读取本地正式常备快照。现存量按当前U9C组织的全部仓库汇总，低于安全库存量时置顶高亮。每次跌破发一条消息；持续低库存不重复发送，恢复后再次跌破重新提醒。新增接收人会在下次回读时收到仍未恢复的提醒。</p>
      <p class="field-help">上次完整回读：{{ stockTime(value.lastSuccessAt) }}<span v-if="value.lastError">；{{ value.lastError }}</span></p>
      <p v-if="hasUnsavedChanges" class="field-help">立即同步使用已保存的提醒设置；修改接收人后请先保存设置。</p>
      <el-button type="primary" :loading="saving" @click="save">保存设置</el-button>
    </el-form>
  </section>
</template>

<style scoped>
.stocked-settings{padding:20px;max-width:900px;overflow:auto}.stocked-settings header{display:flex;align-items:center;justify-content:space-between;margin-bottom:20px}.stocked-settings__actions{display:flex;gap:8px;align-items:center}.stocked-settings__actions :deep(.el-button+.el-button){margin-left:0}.stocked-settings .el-form{max-width:700px}.stocked-settings .el-form-item{margin-top:20px}.stocked-settings :deep(.el-form-item__content){display:block}.stocked-settings .el-select{width:100%;margin-top:10px}.field-help{color:var(--pdm-muted);font-size:12px;line-height:1.7;margin:8px 0 16px}.stocked-settings__error{color:var(--el-color-danger)}
</style>
