<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { reactive, ref, watch } from 'vue'
import type { BomPropertyMapping, BomValidationField, EquipmentTypeDefinition, PdmSystemSettings, ProjectNumberingOptions } from '../types'

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
const storageDraft = reactive<PdmSystemSettings>({ vaultRoot: '', releaseRoot: '', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48, bomDrawingNumberProperty: '物料编码', bomNameProperty: '物料名称', bomDescriptionProperty: '备注信息', bomMaterialProperty: '材质', bomSpecificationProperty: '型号', bomUnitProperty: '单位', bomBrandProperty: '品牌', bomSurfaceTreatmentProperty: '表面处理', bomWeightProperty: '重量', bomPropertyMappings: [], validationRules: { standard: [], nonStandard: [], electrical: [] } })
const equipmentDialogOpen = ref(false)
const equipmentDraft = reactive<EquipmentTypeDefinition>({ code: 0, name: '', isActive: true })
const counterDrafts = ref<Array<{ id: string; name: string; project: number; serial: number }>>([])

const validationKinds = [
  { key: 'standard' as const, label: '标准件 BOM', note: '建议要求型号与品牌' },
  { key: 'nonStandard' as const, label: '非标件 BOM', note: '建议要求材质与表面处理' },
  { key: 'electrical' as const, label: '电气 BOM', note: '不校验材质，建议要求型号与品牌' },
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
  if (mapping.source === 'Pdm') return 'PDM受控版本'
  return mapping.mappingEditable ? 'SolidWorks自定义属性' : 'SolidWorks固定属性'
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
      <el-tab-pane label="BOM属性映射" name="bom-properties">
        <section class="pdm-panel pdm-manager-panel">
          <header class="pdm-manager-heading"><div><h2>SolidWorks属性对应关系</h2><p>列表由服务端的PDM属性目录自动生成；新增PDM属性后会自动出现新行，再由管理员选择对应的SolidWorks属性。配置属性优先于全局属性。</p></div></header>
          <div class="pdm-table-scroll pdm-property-mapping-table">
            <table class="pdm-project-table" aria-label="SolidWorks属性映射列表">
              <thead><tr><th>PDM属性</th><th>数据来源</th><th>SolidWorks对应属性</th><th>规则</th></tr></thead>
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
      <el-tab-pane label="BOM资料校验" name="bom-validation">
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
    <el-dialog v-model="equipmentDialogOpen" :title="`编辑设备类型 ${String(equipmentDraft.code).padStart(2, '0')}`" width="500px" :close-on-click-modal="false">
      <form class="pdm-project-form" @submit.prevent="saveEquipment"><label>设备类型名称<input v-model="equipmentDraft.name" maxlength="100"></label><label class="pdm-checkbox-field"><input v-model="equipmentDraft.isActive" type="checkbox">启用设备类型</label></form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="equipmentDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveEquipment">保存</button></template>
    </el-dialog>
  </section>
</template>
