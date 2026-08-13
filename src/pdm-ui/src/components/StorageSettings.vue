<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { postDesktopMessage } from '../api'
import type { EquipmentTypeDefinition, PdmSystemSettings } from '../types'

const props = defineProps<{
  settings: PdmSystemSettings
  equipmentTypes: EquipmentTypeDefinition[]
  pending: boolean
  onSaveSettings: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
  onSaveEquipmentType: (input: EquipmentTypeDefinition) => Promise<EquipmentTypeDefinition>
}>()

const activeTab = ref('storage')
const storageDraft = reactive<PdmSystemSettings>({ vaultRoot: '', releaseRoot: '' })
const equipmentDialogOpen = ref(false)
const equipmentDraft = reactive<EquipmentTypeDefinition>({ code: 0, name: '', isActive: true })
const desktopAvailable = ref(false)
const startWithWindows = ref(false)
const desktopError = ref('')

watch(() => props.settings, settings => Object.assign(storageDraft, settings), { immediate: true, deep: true })

async function saveStorage() {
  if (!storageDraft.vaultRoot.trim() || !storageDraft.releaseRoot.trim()) {
    ElMessage.warning('请填写图档存档根目录和生产发包根目录')
    return
  }
  try {
    await props.onSaveSettings({ vaultRoot: storageDraft.vaultRoot.trim(), releaseRoot: storageDraft.releaseRoot.trim() })
    ElMessage.success('存储设置已保存，新项目将自动创建项目号目录')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '存储设置保存失败')
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

function receiveDesktopSettings(event: Event) {
  const detail = (event as CustomEvent<{ available?: boolean; startWithWindows?: boolean; error?: string }>).detail
  desktopAvailable.value = detail?.available === true
  startWithWindows.value = detail?.startWithWindows === true
  desktopError.value = detail?.error ?? ''
}

function toggleStartWithWindows() {
  desktopError.value = ''
  postDesktopMessage('desktop-settings-save', { startWithWindows: !startWithWindows.value })
}

onMounted(() => {
  window.addEventListener('pdm-desktop-settings', receiveDesktopSettings)
  postDesktopMessage('desktop-settings-request')
})

onBeforeUnmount(() => window.removeEventListener('pdm-desktop-settings', receiveDesktopSettings))
</script>

<template>
  <section class="pdm-project-manager" aria-label="系统设置">
    <header class="pdm-pagebar"><div><div class="pdm-breadcrumb">系统管理 <span>/</span> 系统设置</div><h1>系统设置</h1><p>统一维护项目存储根目录和设备类型基础资料。</p></div></header>
    <el-tabs v-model="activeTab" class="pdm-settings-tabs">
      <el-tab-pane label="存档位置" name="storage">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>项目文件夹规则</h2><p>创建项目时不再填写路径，系统自动使用“根目录\项目号”。</p></div></header>
          <div class="pdm-settings-form"><label>图档存档根目录<input v-model="storageDraft.vaultRoot" placeholder="例如 D:\PDM\Vault"><small>示例：{{ storageDraft.vaultRoot || '未设置' }}\P700001</small></label><label>生产发包根目录<input v-model="storageDraft.releaseRoot" placeholder="例如 D:\PDM\Release"><small>示例：{{ storageDraft.releaseRoot || '未设置' }}\P700001</small></label></div>
          <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveStorage">保存存储设置</button></div>
        </section>
      </el-tab-pane>
      <el-tab-pane label="设备类型" name="equipment">
        <section class="pdm-panel pdm-project-list">
          <header class="pdm-panel-heading"><div><h2>设备类型设置</h2><small>编码固定为00–99；创建项目时只显示启用项。</small></div></header>
          <div class="pdm-table-scroll pdm-equipment-table"><table class="pdm-project-table"><thead><tr><th>编码</th><th>设备类型名称</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="item in equipmentTypes" :key="item.code"><td><strong>{{ String(item.code).padStart(2, '0') }}</strong></td><td>{{ item.name }}</td><td>{{ item.isActive === false ? '停用' : '启用' }}</td><td><button type="button" class="pdm-text-action" @click="editEquipmentType(item)">编辑</button></td></tr></tbody></table></div>
        </section>
      </el-tab-pane>
      <el-tab-pane v-if="desktopAvailable" label="Windows客户端" name="desktop">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>客户端常驻设置</h2><p>关闭窗口后，客户端继续在Windows右下角通知区域运行。</p></div></header>
          <div class="pdm-setting-list">
            <article><div><small>启动方式</small><strong>随电脑启动</strong><small>启动后直接进入通知区域；双击PDM图标恢复窗口，右键图标可退出。</small><small v-if="desktopError" class="is-warn">{{ desktopError }}</small></div><button type="button" class="pdm-secondary-action" @click="toggleStartWithWindows">{{ startWithWindows ? '已开启' : '已关闭' }}</button></article>
          </div>
        </section>
      </el-tab-pane>
    </el-tabs>
    <el-dialog v-model="equipmentDialogOpen" :title="`编辑设备类型 ${String(equipmentDraft.code).padStart(2, '0')}`" width="500px" :close-on-click-modal="false">
      <form class="pdm-project-form" @submit.prevent="saveEquipment"><label>设备类型名称<input v-model="equipmentDraft.name" maxlength="100"></label><label class="pdm-checkbox-field"><input v-model="equipmentDraft.isActive" type="checkbox">启用设备类型</label></form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="equipmentDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveEquipment">保存</button></template>
    </el-dialog>
  </section>
</template>
