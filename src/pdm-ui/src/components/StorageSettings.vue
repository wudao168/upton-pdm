<script setup lang="ts">
import CompanyName from './CompanyName.vue'
import { ElMessage } from '../statusMessage'
import { reactive, ref, useId, watch } from 'vue'
import StorageDirectoryList from './StorageDirectoryList.vue'
import PreviewConversionSettings from './PreviewConversionSettings.vue'
import StandardStructureStorageSettings from './StandardStructureStorageSettings.vue'
import ServerStorageSettings from './ServerStorageSettings.vue'
import PdfTitleBlockFillSettings from './PdfTitleBlockFillSettings.vue'
import BrowserCadArchiveSettings from './BrowserCadArchiveSettings.vue'
import type { BomPropertyMapping, BomValidationField, EquipmentTypeDefinition, PdmSystemSettings, ProjectNumberingOptions } from '../types'

const props = withDefaults(defineProps<{
  administrator?: boolean
  active?: boolean
  canManageStorage?: boolean
  canManageFolders?: boolean
  settings: PdmSystemSettings
  token?: string
  companyId?: string
  equipmentTypes: EquipmentTypeDefinition[]
  numberingOptions: ProjectNumberingOptions
  pending: boolean
  onSaveSettings: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
  onSaveEquipmentType: (input: EquipmentTypeDefinition) => Promise<EquipmentTypeDefinition>
  onUpdateCounters: (organizationId: string, currentProjectSequence: number, currentSerialSequence: number) => Promise<ProjectNumberingOptions>
}>(), { canManageStorage: true, active: true })

const activeTab = ref('storage')
const storageId = useId()
const projectDirectories = [
  { key: 'vaultRoot', label: '图档存档根目录' },
  { key: 'releaseRoot', label: '生产发包根目录' },
  { key: 'materialAttachmentRoot', label: '料品资料存档根目录' },
] as const
watch(() => [props.canManageStorage, props.canManageFolders], ([storage, folders]) => {
  if (storage === false) activeTab.value = 'folder-templates'
  else if (!folders && activeTab.value === 'folder-templates') activeTab.value = 'storage'
}, { immediate: true })
const storageDraft = reactive<PdmSystemSettings>({ vaultRoot: '', releaseRoot: '', materialAttachmentRoot: '', checkoutHeartbeatSeconds: 60, checkoutLeaseMinutes: 5, checkoutOfflineGraceMinutes: 30, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutOverdueReleaseGraceHours: 12, checkoutForceReleaseHours: 48, checkoutAutoReleaseEnabled: true, checkoutIdleReleaseMinutes: 30, checkoutReleaseCountdownSeconds: 60, bomDrawingNumberProperty: '物料编码', bomNameProperty: '物料名称', bomDescriptionProperty: '备注信息', bomMaterialProperty: '材质', bomSpecificationProperty: '型号', bomUnitProperty: '单位', bomBrandProperty: '品牌', bomSurfaceTreatmentProperty: '表面处理', bomWeightProperty: '重量', bomPropertyMappings: [], validationRules: { standard: [], nonStandard: [], electrical: [] } })
const equipmentDialogOpen = ref(false)
const equipmentDraft = reactive<EquipmentTypeDefinition>({ code: 0, name: '', isActive: true })
const counterDrafts = ref<Array<{ id: string; name: string; project: number; serial: number }>>([])

const validationKinds = [
  { key: 'standard' as const, label: '标准件 BOM', note: '建议要求型号与品牌' },
  { key: 'nonStandard' as const, label: '非标件 BOM', note: '建议要求材质与表面处理' },
  { key: 'electrical' as const, label: '电气件BOM', note: '不校验材质，建议要求型号与品牌' },
]
const validationFields: Array<{ key: BomValidationField; label: string; core?: boolean }> = [
  { key: 'drawingNumber', label: '物料编码', core: true },
  { key: 'name', label: '物料名称', core: true },
  { key: 'unit', label: '单位', core: true },
  { key: 'quantity', label: '数量', core: true },
  { key: 'revision', label: '版本', core: true },
  { key: 'specification', label: '型号' },
  { key: 'brand', label: '品牌' },
  { key: 'material', label: '材质' },
  { key: 'surfaceTreatment', label: '表面处理' },
  { key: 'weight', label: '重量' },
  { key: 'remark', label: '备注' },
]
const knownSolidWorksProperties = [
  '物料分类', '易损件', '单位', '物料编码', '物料名称', '零件名称', '型号', '备注信息', '备注', '品牌', '材质', '材料',
  '表面处理', '重量', '数量', '图号', '名称', '规格', '项目号', '项目名称', '热处理', '设计', '制图', '校对', '批准',
]

watch(() => props.settings, settings => Object.assign(storageDraft, {
  ...settings,
  checkoutOverdueReleaseGraceHours: settings.checkoutOverdueReleaseGraceHours ?? 12,
  checkoutAutoReleaseEnabled: settings.checkoutAutoReleaseEnabled ?? true,
  checkoutIdleReleaseMinutes: settings.checkoutIdleReleaseMinutes ?? 30,
  checkoutReleaseCountdownSeconds: settings.checkoutReleaseCountdownSeconds ?? 60,
  bomPropertyMappings: (settings.bomPropertyMappings ?? []).map(mapping => ({ ...mapping })),
  validationRules: {
    standard: [...settings.validationRules.standard],
    nonStandard: [...settings.validationRules.nonStandard],
    electrical: [...settings.validationRules.electrical],
  },
}), { immediate: true, deep: true })
watch(() => props.numberingOptions.organizations, organizations => {
  counterDrafts.value = organizations.map(item => ({ id: item.id, name: item.name, project: item.currentProjectSequence, serial: item.currentSerialSequence }))
}, { immediate: true, deep: true })

async function saveStorage() {
  if (!storageDraft.vaultRoot.trim() || !storageDraft.releaseRoot.trim() || !storageDraft.materialAttachmentRoot.trim()) {
    ElMessage.warning('请填写图档、生产发包和料品资料存档根目录')
    return
  }
  try {
    await props.onSaveSettings({ ...storageDraft, vaultRoot: storageDraft.vaultRoot.trim(), releaseRoot: storageDraft.releaseRoot.trim(), materialAttachmentRoot: storageDraft.materialAttachmentRoot.trim() })
    ElMessage.success('存储设置已保存，新项目将自动创建项目号目录')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '存储设置保存失败')
  }
}

async function saveBomMappings() {
  const mappings = storageDraft.bomPropertyMappings.filter(mapping => mapping.mappingEditable)
  if (mappings.some(mapping => !mapping.solidWorksProperty.trim())) {
    ElMessage.warning('SolidWorks属性名称不能为空')
    return
  }
  try {
    const property = (key: string, fallback: string) => storageDraft.bomPropertyMappings.find(mapping => mapping.pdmPropertyKey === key)?.solidWorksProperty || fallback
    await props.onSaveSettings({
      ...storageDraft,
      bomDrawingNumberProperty: property('drawingNumber', storageDraft.bomDrawingNumberProperty),
      bomNameProperty: property('name', storageDraft.bomNameProperty),
      bomDescriptionProperty: property('remark', storageDraft.bomDescriptionProperty),
      bomMaterialProperty: property('material', storageDraft.bomMaterialProperty),
      bomSpecificationProperty: property('specification', storageDraft.bomSpecificationProperty),
      bomUnitProperty: property('unit', storageDraft.bomUnitProperty),
      bomBrandProperty: property('brand', storageDraft.bomBrandProperty),
      bomSurfaceTreatmentProperty: property('surfaceTreatment', storageDraft.bomSurfaceTreatmentProperty),
      bomWeightProperty: property('weight', storageDraft.bomWeightProperty),
    })
    ElMessage.success('BOM属性映射已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'BOM属性映射保存失败')
  }
}

function mappingOptions(mapping: BomPropertyMapping) {
  return [...new Set([...knownSolidWorksProperties, mapping.solidWorksProperty].filter(Boolean))]
}

function mappingSource(mapping: BomPropertyMapping) {
  if (mapping.source === 'Assembly') return '装配结构自动计算'
  if (mapping.source === 'Pdm') return 'PLM受控版本'
  return mapping.mappingEditable ? 'SolidWorks自定义属性' : 'SolidWorks固定属性'
}

async function saveCheckoutPolicy() {
  const durations = [storageDraft.checkoutOverdueHours, storageDraft.checkoutOverdueReleaseGraceHours ?? 12]
  if (durations.some(value => !Number.isInteger(value) || value < 1 || value > 720)) {
    ElMessage.warning('占用提醒和提醒后宽限须分别填写1至720之间的整数小时')
    return
  }
  try {
    await props.onSaveSettings({ ...storageDraft })
    ElMessage.success('编辑权限防占用策略已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '编辑权限策略保存失败')
  }
}

async function saveValidationRules() {
  try {
    await props.onSaveSettings({
      ...storageDraft,
      validationRules: {
        standard: [...storageDraft.validationRules.standard],
        nonStandard: [...storageDraft.validationRules.nonStandard],
        electrical: [...storageDraft.validationRules.electrical],
      },
    })
    ElMessage.success('三类BOM资料校验规则已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'BOM资料校验规则保存失败')
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
    <el-tabs v-model="activeTab" class="pdm-settings-tabs">
      <el-tab-pane v-if="canManageStorage !== false" label="项目编号" name="numbering">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>组织编号流水</h2><p>设置下一次项目编号和设备序列号使用的流水基线。</p></div></header>
          <div class="pdm-table-scroll"><table class="pdm-project-table"><thead><tr><th>组织</th><th>当前项目流水</th><th>当前序列流水</th><th>操作</th></tr></thead><tbody><tr v-for="row in counterDrafts" :key="row.id"><td><strong><CompanyName :name="row.name" /></strong></td><td><input v-model.number="row.project" class="pdm-table-input" type="number" min="0" max="99999"></td><td><input v-model.number="row.serial" class="pdm-table-input" type="number" min="0" max="9999999"></td><td><button type="button" class="pdm-text-action" :disabled="pending" @click="saveCounters(row)">保存</button></td></tr></tbody></table></div>
        </section>
      </el-tab-pane>
      <el-tab-pane v-if="canManageStorage !== false" label="存档位置" name="storage" class="pdm-storage-location-pane">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><h2>项目文件夹规则</h2></header>
          <StorageDirectoryList label="项目存档目录">
            <tr v-for="field in projectDirectories" :key="field.key">
              <th scope="row"><label :for="`${storageId}-${field.key}`">{{ field.label }}</label></th>
              <td><input :id="`${storageId}-${field.key}`" v-model="storageDraft[field.key]" class="pdm-table-input"></td>
            </tr>
          </StorageDirectoryList>
          <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveStorage">保存存储设置</button></div>
        </section>
        <ServerStorageSettings v-if="token" :token="token" />
        <StandardStructureStorageSettings v-if="token" :token="token" />
        <BrowserCadArchiveSettings v-if="token" :token="token" />
      </el-tab-pane>
      <el-tab-pane v-if="canManageFolders && $slots.folders" label="文件夹模板" name="folder-templates" lazy>
        <slot name="folders" />
      </el-tab-pane>
      <el-tab-pane v-if="canManageStorage !== false" label="图纸转换" name="preview-conversion" lazy>
        <PreviewConversionSettings v-if="token" :key="companyId" :token="token" :active="active && activeTab === 'preview-conversion'" />
      </el-tab-pane>
      <el-tab-pane v-if="canManageStorage !== false" label="PDF属性填写" name="pdf-title-block">
        <PdfTitleBlockFillSettings :settings="settings" :administrator="administrator ?? false" :pending="pending" :on-save-settings="onSaveSettings" />
      </el-tab-pane>
      <el-tab-pane v-if="canManageStorage !== false" label="编辑权限" name="checkout-policy">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>图档编辑与自动释放</h2><p>关闭图档时，插件先保留必要的草稿，再释放编辑权限。长期占用只提醒编辑人。提醒后仍占用超过宽限期时，系统自动释放。</p></div></header>
          <div class="pdm-settings-form pdm-lock-policy-grid pdm-settings-grid--four">
            <label>插件心跳（秒）<input v-model.number="storageDraft.checkoutHeartbeatSeconds" type="number" min="30" max="600"><small>建议60秒</small></label>
            <label>编辑租约（分钟）<input v-model.number="storageDraft.checkoutLeaseMinutes" type="number" min="2" max="60"><small>建议5分钟，至少为心跳的两倍</small></label>
            <label>离线宽限（分钟）<input v-model.number="storageDraft.checkoutOfflineGraceMinutes" type="number" min="5" max="1440"><small>建议30分钟，之后可人工接管</small></label>
            <label>占用提醒（小时）<input v-model.number="storageDraft.checkoutOverdueHours" type="number" min="1" max="720"><small>默认24小时，从获取编辑权限时起算。</small></label>
            <label>提醒后宽限（小时）<input v-model.number="storageDraft.checkoutOverdueReleaseGraceHours" type="number" min="1" max="720"><small>默认12小时，从发出提醒时起算。</small></label>
            <label><span>启用自动释放</span><input v-model="storageDraft.checkoutAutoReleaseEnabled" type="checkbox"><small>控制插件无修改闲置释放和长期占用到期释放。</small></label>
            <label>无修改闲置（分钟）<input v-model.number="storageDraft.checkoutIdleReleaseMinutes" type="number" min="5" max="1440"><small>建议30分钟，须由插件确认无未提交修改。</small></label>
            <label>释放倒计时（秒）<input v-model.number="storageDraft.checkoutReleaseCountdownSeconds" type="number" min="30" max="300"><small>建议60秒，可点击“继续编辑”延期。</small></label>
          </div>
          <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveCheckoutPolicy">保存编辑权限策略</button></div>
        </section>
      </el-tab-pane>
      <el-tab-pane v-if="canManageStorage !== false" label="设备类型" name="equipment">
        <section class="pdm-panel pdm-project-list">
          <header class="pdm-panel-heading"><div><h2>设备类型设置</h2><small>编码固定为00–99；创建项目时只显示启用项。</small></div></header>
          <div class="pdm-table-scroll pdm-equipment-table"><table class="pdm-project-table"><thead><tr><th>编码</th><th>设备类型名称</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="item in equipmentTypes" :key="item.code"><td><strong>{{ String(item.code).padStart(2, '0') }}</strong></td><td>{{ item.name }}</td><td>{{ item.isActive === false ? '停用' : '启用' }}</td><td><button type="button" class="pdm-text-action" @click="editEquipmentType(item)">编辑</button></td></tr></tbody></table></div>
        </section>
      </el-tab-pane>
      <el-tab-pane v-if="canManageStorage !== false" label="BOM属性映射" name="bom-properties">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>SolidWorks属性对应关系</h2><p>列表由服务端的PLM属性目录自动生成；新增PLM属性后会自动出现新行，再由管理员选择对应的SolidWorks属性。配置属性优先于全局属性。</p></div></header>
          <div class="pdm-table-scroll pdm-property-mapping-table">
            <table class="pdm-project-table" aria-label="SolidWorks属性映射列表">
              <thead><tr><th>PLM属性</th><th>数据来源</th><th>SolidWorks对应属性</th><th>规则</th></tr></thead>
              <tbody>
                <tr v-for="mapping in storageDraft.bomPropertyMappings" :key="mapping.pdmPropertyKey">
                  <td><strong>{{ mapping.pdmPropertyName }}</strong><small>{{ mapping.pdmPropertyKey }}</small></td>
                  <td>{{ mappingSource(mapping) }}</td>
                  <td>
                    <template v-if="mapping.mappingEditable">
                      <input v-model="mapping.solidWorksProperty" class="pdm-table-input" :list="`solidworks-property-options-${mapping.pdmPropertyKey}`" :aria-label="`${mapping.pdmPropertyName}对应SolidWorks属性`">
                      <datalist :id="`solidworks-property-options-${mapping.pdmPropertyKey}`">
                        <option v-for="option in mappingOptions(mapping)" :key="option" :value="option" />
                      </datalist>
                    </template>
                    <span v-else-if="mapping.solidWorksProperty" class="pdm-mapping-fixed">{{ mapping.solidWorksProperty }}</span>
                    <span v-else class="pdm-muted">不读取SolidWorks属性</span>
                  </td>
                  <td>{{ mapping.mappingEditable ? '人工选择' : '系统规则' }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveBomMappings">保存属性映射</button></div>
        </section>
      </el-tab-pane>
      <el-tab-pane v-if="canManageStorage !== false" label="BOM资料校验" name="bom-validation">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>分类必填规则</h2><p>资料状态、设变提交和正式发布均由服务端按此规则校验；正式BOM版本会保留当时的规则快照。</p></div></header>
          <div class="pdm-bom-validation-grid">
            <article v-for="validationKind in validationKinds" :key="validationKind.key" class="pdm-bom-validation-card">
              <header><strong>{{ validationKind.label }}</strong><small>{{ validationKind.note }}</small></header>
              <div class="pdm-bom-validation-fields">
                <label
                  v-for="field in validationFields.filter(item => !(validationKind.key === 'electrical' && item.key === 'material'))"
                  :key="field.key"
                  class="pdm-checkbox-field"
                >
                  <input v-model="storageDraft.validationRules[validationKind.key]" type="checkbox" :value="field.key" :disabled="field.core">
                  {{ field.label }}<small v-if="field.core">系统必填</small>
                </label>
              </div>
            </article>
          </div>
          <p class="pdm-settings-note">物料分类本身始终必须有效；电气件不要求材质。修改规则只影响当前工作区和后续发布，不重算已有正式BOM版本。</p>
          <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveValidationRules">保存校验规则</button></div>
        </section>
      </el-tab-pane>
    </el-tabs>
    <el-dialog v-model="equipmentDialogOpen" :title="`编辑设备类型 ${String(equipmentDraft.code).padStart(2, '0')}`" width="500px" :close-on-click-modal="!pending">
      <form class="pdm-project-form" @submit.prevent="saveEquipment"><label>设备类型名称<input v-model="equipmentDraft.name" maxlength="100"></label><label class="pdm-checkbox-field"><input v-model="equipmentDraft.isActive" type="checkbox">启用设备类型</label></form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="equipmentDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveEquipment">保存</button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.pdm-storage-location-pane { overflow-y: auto; }
.pdm-storage-location-pane :deep(.pdm-manager-panel) { height: auto; flex: 0 0 auto; overflow: visible; }
</style>
