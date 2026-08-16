<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { reactive, ref, watch } from 'vue'
import type { EquipmentTypeDefinition, PdmSystemSettings, ProjectNumberingOptions } from '../types'

const props = defineProps<{
  settings: PdmSystemSettings
  equipmentTypes: EquipmentTypeDefinition[]
  numberingOptions: ProjectNumberingOptions
  pending: boolean
  onSaveSettings: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
  onSaveEquipmentType: (input: EquipmentTypeDefinition) => Promise<EquipmentTypeDefinition>
  onUpdateCounters: (organizationId: string, currentProjectSequence: number, currentSerialSequence: number) => Promise<ProjectNumberingOptions>
}>()

const activeTab = ref('storage')
const storageDraft = reactive<PdmSystemSettings>({ vaultRoot: '', releaseRoot: '', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48 })
const equipmentDialogOpen = ref(false)
const equipmentDraft = reactive<EquipmentTypeDefinition>({ code: 0, name: '', isActive: true })
const counterDrafts = ref<Array<{ id: string; name: string; project: number; serial: number }>>([])

watch(() => props.settings, settings => Object.assign(storageDraft, settings), { immediate: true, deep: true })
watch(() => props.numberingOptions.organizations, organizations => {
  counterDrafts.value = organizations.map(item => ({ id: item.id, name: item.name, project: item.currentProjectSequence, serial: item.currentSerialSequence }))
}, { immediate: true, deep: true })

async function saveStorage() {
  if (!storageDraft.vaultRoot.trim() || !storageDraft.releaseRoot.trim()) {
    ElMessage.warning('请填写图档存档根目录和生产发包根目录')
    return
  }
  try {
    await props.onSaveSettings({ ...storageDraft, vaultRoot: storageDraft.vaultRoot.trim(), releaseRoot: storageDraft.releaseRoot.trim() })
    ElMessage.success('存储设置已保存，新项目将自动创建项目号目录')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '存储设置保存失败')
  }
}

async function saveCheckoutPolicy() {
  const ordered = [storageDraft.checkoutReminderHours, storageDraft.checkoutStrongReminderHours, storageDraft.checkoutOverdueHours, storageDraft.checkoutForceReleaseHours]
  if (ordered.some(value => !Number.isInteger(value) || value < 1) || ordered.some((value, index) => index > 0 && value <= ordered[index - 1])) {
    ElMessage.warning('提醒、强提醒、超时和强制释放时间必须为依次增大的整数')
    return
  }
  try {
    await props.onSaveSettings({ ...storageDraft })
    ElMessage.success('编辑权限防占用策略已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '编辑权限策略保存失败')
  }
}

function editEquipmentType(item: EquipmentTypeDefinition) {
  Object.assign(equipmentDraft, item)
  equipmentDraft.isActive = item.isActive !== false
  equipmentDialogOpen.value = true
}

async function saveEquipment() {
  if (!equipmentDraft.name.trim()) {
    ElMessage.warning('请填写设备类型名称')
    return
  }
  try {
    await props.onSaveEquipmentType({ code: equipmentDraft.code, name: equipmentDraft.name.trim(), isActive: equipmentDraft.isActive })
    equipmentDialogOpen.value = false
    ElMessage.success('设备类型已更新')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '设备类型保存失败')
  }
}

async function saveCounters(row: { id: string; project: number; serial: number }) {
  try {
    await props.onUpdateCounters(row.id, row.project, row.serial)
    ElMessage.success('编号流水基线已更新')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '编号流水保存失败')
  }
}

</script>

<template>
  <section class="pdm-project-manager" aria-label="系统设置">
    <header class="pdm-pagebar"><div><div class="pdm-breadcrumb">系统管理 <span>/</span> 编号与存储</div><h1>编号与存储</h1><p>统一维护项目编号、存储位置、编辑权限策略和设备类型。</p></div></header>
    <el-tabs v-model="activeTab" class="pdm-settings-tabs">
      <el-tab-pane label="项目编号" name="numbering">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>组织编号流水</h2><p>设置下一次项目编号和设备序列号使用的流水基线。</p></div></header>
          <div class="pdm-table-scroll"><table class="pdm-project-table"><thead><tr><th>组织</th><th>当前项目流水</th><th>当前序列流水</th><th>操作</th></tr></thead><tbody><tr v-for="row in counterDrafts" :key="row.id"><td><strong>{{ row.name }}</strong></td><td><input v-model.number="row.project" class="pdm-table-input" type="number" min="0" max="99999"></td><td><input v-model.number="row.serial" class="pdm-table-input" type="number" min="0" max="9999999"></td><td><button type="button" class="pdm-text-action" :disabled="pending" @click="saveCounters(row)">保存</button></td></tr></tbody></table></div>
        </section>
      </el-tab-pane>
      <el-tab-pane label="存档位置" name="storage">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>项目文件夹规则</h2><p>创建项目时不再填写路径，系统自动使用“根目录\项目号”。</p></div></header>
          <div class="pdm-settings-form"><label>图档存档根目录<input v-model="storageDraft.vaultRoot" placeholder="例如 D:\PDM\Vault"><small>示例：{{ storageDraft.vaultRoot || '未设置' }}\P700001</small></label><label>生产发包根目录<input v-model="storageDraft.releaseRoot" placeholder="例如 D:\PDM\Release"><small>示例：{{ storageDraft.releaseRoot || '未设置' }}\P700001</small></label></div>
          <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveStorage">保存存储设置</button></div>
        </section>
      </el-tab-pane>
      <el-tab-pane label="编辑权限" name="checkout-policy">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>防止长时间占用</h2><p>租约过期只标记离线，不自动释放；达到强制释放时限后仍需授权人员填写原因。</p></div></header>
          <div class="pdm-settings-form pdm-lock-policy-grid">
            <label>插件心跳（秒）<input v-model.number="storageDraft.checkoutHeartbeatSeconds" type="number" min="30" max="600"><small>建议180秒</small></label>
            <label>编辑租约（分钟）<input v-model.number="storageDraft.checkoutLeaseMinutes" type="number" min="2" max="60"><small>建议15分钟，至少为心跳的两倍</small></label>
            <label>离线宽限（分钟）<input v-model.number="storageDraft.checkoutOfflineGraceMinutes" type="number" min="5" max="1440"><small>建议60分钟</small></label>
            <label>首次提醒（小时）<input v-model.number="storageDraft.checkoutReminderHours" type="number" min="1" max="720"><small>建议4小时</small></label>
            <label>强提醒（小时）<input v-model.number="storageDraft.checkoutStrongReminderHours" type="number" min="2" max="720"><small>建议8小时</small></label>
            <label>超时标记（小时）<input v-model.number="storageDraft.checkoutOverdueHours" type="number" min="3" max="720"><small>建议24小时</small></label>
            <label>允许强制释放（小时）<input v-model.number="storageDraft.checkoutForceReleaseHours" type="number" min="4" max="720"><small>建议48小时；旧会话将禁止提交</small></label>
          </div>
          <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveCheckoutPolicy">保存编辑权限策略</button></div>
        </section>
      </el-tab-pane>
      <el-tab-pane label="设备类型" name="equipment">
        <section class="pdm-panel pdm-project-list">
          <header class="pdm-panel-heading"><div><h2>设备类型设置</h2><small>编码固定为00–99；创建项目时只显示启用项。</small></div></header>
          <div class="pdm-table-scroll pdm-equipment-table"><table class="pdm-project-table"><thead><tr><th>编码</th><th>设备类型名称</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="item in equipmentTypes" :key="item.code"><td><strong>{{ String(item.code).padStart(2, '0') }}</strong></td><td>{{ item.name }}</td><td>{{ item.isActive === false ? '停用' : '启用' }}</td><td><button type="button" class="pdm-text-action" @click="editEquipmentType(item)">编辑</button></td></tr></tbody></table></div>
        </section>
      </el-tab-pane>
    </el-tabs>
    <el-dialog v-model="equipmentDialogOpen" :title="`编辑设备类型 ${String(equipmentDraft.code).padStart(2, '0')}`" width="500px" :close-on-click-modal="false">
      <form class="pdm-project-form" @submit.prevent="saveEquipment"><label>设备类型名称<input v-model="equipmentDraft.name" maxlength="100"></label><label class="pdm-checkbox-field"><input v-model="equipmentDraft.isActive" type="checkbox">启用设备类型</label></form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="equipmentDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveEquipment">保存</button></template>
    </el-dialog>
  </section>
</template>
