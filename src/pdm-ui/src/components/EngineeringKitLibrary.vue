<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getEngineeringKitOptions, listEngineeringKits, listMaterials, publishEngineeringKit, saveEngineeringKit, saveEngineeringKitOptions } from '../api'
import type { EngineeringKit, EngineeringKitOptionCatalog, EngineeringKitOptionEntry, PdmMaterial } from '../types'

const props = defineProps<{ token: string; canManage: boolean }>()
const loading = ref(false)
const saving = ref(false)
const publishing = ref(false)
const query = ref('')
const brandFilter = ref('')
const kits = ref<EngineeringKit[]>([])
const materials = ref<PdmMaterial[]>([])
const editorOpen = ref(false)
const editing = ref<EngineeringKit | null>(null)
const kitOptions = ref<EngineeringKitOptionCatalog>({ standardCodes: [], categoryCodes: [] })
const optionDialogOpen = ref(false)
const optionSaving = ref(false)
const optionDraft = ref<EngineeringKitOptionCatalog>({ standardCodes: [], categoryCodes: [] })
type OptionListKey = 'standardCodes' | 'categoryCodes'
const optionSection = ref<OptionListKey>('standardCodes')
const optionNewName = ref('')
const optionNewCode = ref('')
const optionEditingIndex = ref(-1)
const optionEditingName = ref('')
const optionEditingCode = ref('')
const form = ref({
  name: '', brand: '', description: '', changeNote: '',
  modelMode: 'Auto' as 'Auto' | 'Manual', model: '', standardName: '', categoryName: '',
  components: [] as Array<{ materialId: string; quantity: number; isOptional: false }>,
})

const eligibleMaterials = computed(() => materials.value.filter(item => !item.isArchived && item.approvalStatus === 'Approved'))
const brandOptions = computed(() => [...new Set(kits.value.map(item => item.brand?.trim()).filter((value): value is string => Boolean(value)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const filteredKits = computed(() => {
  const keyword = query.value.trim().toLowerCase()
  return kits.value.filter(item => (!brandFilter.value || item.brand === brandFilter.value)
    && (!keyword || [item.code, item.model, item.name, item.brand].some(value => value?.toLowerCase().includes(keyword))))
})
const optionLists = computed(() => optionDraft.value[optionSection.value])
function codeForName(entries: EngineeringKitOptionEntry[], name: string) {
  const normalized = name.trim()
  if (!normalized) return ''
  return entries.find(item => item.name.trim().toLowerCase() === normalized.toLowerCase())?.code ?? ''
}
const selectedStandardCode = computed(() => codeForName(kitOptions.value.standardCodes, form.value.standardName))
const selectedCategoryCode = computed(() => codeForName(kitOptions.value.categoryCodes, form.value.categoryName))
const autoModelHint = computed(() => `${selectedStandardCode.value || '标准代码'}-${selectedCategoryCode.value || '分类代码'}-序列号`)

function draftRevision(kit?: EngineeringKit | null) {
  return kit ? [...kit.revisions].filter(item => item.state === 'Draft').sort((a, b) => b.versionNumber - a.versionNumber)[0] : undefined
}
function releasedRevision(kit?: EngineeringKit | null) {
  return kit?.revisions.find(item => item.id === kit.currentReleasedRevisionId)
}
function displayRevision(kit: EngineeringKit) {
  return draftRevision(kit) ?? releasedRevision(kit)
}
function selectedMaterial(materialId: string) {
  return eligibleMaterials.value.find(item => item.id === materialId)
}
function materialLabel(material: PdmMaterial) {
  return `${material.materialCode} · ${material.name}`
}

async function load() {
  loading.value = true
  try {
    const [loadedKits, loadedMaterials, loadedOptions] = await Promise.all([
      listEngineeringKits(props.token, !props.canManage),
      props.canManage ? listMaterials(props.token, '', false, 500) : Promise.resolve([]),
      getEngineeringKitOptions(props.token).catch(() => kitOptions.value),
    ])
    kits.value = loadedKits
    materials.value = loadedMaterials
    kitOptions.value = loadedOptions
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件加载失败')
  } finally {
    loading.value = false
  }
}

function openEditor(kit?: EngineeringKit) {
  editing.value = kit ?? null
  const revision = kit ? displayRevision(kit) : undefined
  // 新建套件时默认选中维护好的第一项标准/分类，减少手工选择。
  const defaultStandard = kitOptions.value.standardCodes[0]?.name ?? ''
  const defaultCategory = kitOptions.value.categoryCodes[0]?.name ?? ''
  form.value = {
    name: kit?.name ?? '',
    brand: kit?.brand ?? '',
    description: kit?.description ?? '',
    changeNote: draftRevision(kit)?.changeNote ?? '',
    modelMode: kit?.modelMode === 'Manual' ? 'Manual' : 'Auto',
    model: kit?.modelMode === 'Manual' ? kit?.model ?? '' : '',
    standardName: kit ? kit.standardName ?? '' : defaultStandard,
    categoryName: kit ? kit.categoryName ?? '' : defaultCategory,
    components: revision?.components.map(item => ({ materialId: item.materialId, quantity: item.quantity, isOptional: false })) ?? [],
  }
  editorOpen.value = true
}
function closeEditor() {
  editorOpen.value = false
  editing.value = null
}
function addComponent() {
  form.value.components.push({ materialId: '', quantity: 1, isOptional: false })
}
function removeComponent(index: number) {
  form.value.components.splice(index, 1)
}

async function save() {
  if (!form.value.name.trim()) return ElMessage.warning('请填写套件名称')
  if (!form.value.brand.trim()) return ElMessage.warning('请填写套件品牌')
  if (form.value.modelMode === 'Manual' && !form.value.model.trim()) return ElMessage.warning('请填写套件型号，或改为自动生成')
  if (form.value.modelMode === 'Auto' && (!form.value.standardName.trim() || !form.value.categoryName.trim()))
    return ElMessage.warning('自动生成型号需要先选择标准名称和分类名称')
  if (form.value.components.length === 0) return ElMessage.warning('套件至少需要一个明细物料')
  if (form.value.components.some(item => !item.materialId || item.quantity <= 0)) return ElMessage.warning('请完整填写物料和每套数量')
  if (new Set(form.value.components.map(item => item.materialId)).size !== form.value.components.length) return ElMessage.warning('同一真实物料只能出现一次')
  saving.value = true
  try {
    const saved = await saveEngineeringKit({
      name: form.value.name.trim(),
      brand: form.value.brand.trim(),
      description: form.value.description.trim(),
      changeNote: form.value.changeNote.trim(),
      modelMode: form.value.modelMode,
      model: form.value.model.trim(),
      standardName: form.value.standardName.trim(),
      standardCode: selectedStandardCode.value,
      categoryName: form.value.categoryName.trim(),
      categoryCode: selectedCategoryCode.value,
      components: form.value.components.map((item, index) => ({ ...item, isOptional: false, sortOrder: index + 1 })),
      expectedRowVersion: editing.value?.rowVersion,
    }, props.token, editing.value?.id)
    editing.value = saved
    kits.value = kits.value.some(item => item.id === saved.id) ? kits.value.map(item => item.id === saved.id ? saved : item) : [saved, ...kits.value]
    ElMessage.success('套件草稿已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件保存失败')
  } finally {
    saving.value = false
  }
}

function openOptionMaintenance() {
  optionDraft.value = {
    standardCodes: kitOptions.value.standardCodes.map(item => ({ ...item })),
    categoryCodes: kitOptions.value.categoryCodes.map(item => ({ ...item })),
  }
  optionSection.value = 'standardCodes'
  optionNewName.value = ''
  optionNewCode.value = ''
  cancelOptionEdit()
  optionDialogOpen.value = true
}

function addOption() {
  const name = optionNewName.value.trim()
  const code = optionNewCode.value.trim()
  if (!name || !code) return
  if (optionLists.value.some(item => item.name.trim().toLowerCase() === name.toLowerCase())) return
  optionLists.value.push({ name, code })
  optionNewName.value = ''
  optionNewCode.value = ''
}

function removeOption(index: number) {
  optionLists.value.splice(index, 1)
  cancelOptionEdit()
}

function moveOption(index: number, direction: -1 | 1) {
  const values = optionLists.value
  const target = index + direction
  if (target < 0 || target >= values.length) return
  const [moved] = values.splice(index, 1)
  values.splice(target, 0, moved)
  cancelOptionEdit()
}

function startOptionEdit(index: number) {
  optionEditingIndex.value = index
  optionEditingName.value = optionLists.value[index]?.name ?? ''
  optionEditingCode.value = optionLists.value[index]?.code ?? ''
}

function commitOptionEdit() {
  const index = optionEditingIndex.value
  const name = optionEditingName.value.trim()
  const code = optionEditingCode.value.trim()
  if (index < 0) return
  if (!name || !code) return cancelOptionEdit()
  optionLists.value.splice(index, 1, { name, code })
  cancelOptionEdit()
}

function cancelOptionEdit() {
  optionEditingIndex.value = -1
  optionEditingName.value = ''
  optionEditingCode.value = ''
}

async function saveOptionMaintenance() {
  optionSaving.value = true
  try {
    kitOptions.value = await saveEngineeringKitOptions(optionDraft.value, props.token)
    optionDialogOpen.value = false
    ElMessage.success('标准代码与分类代码已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '代码选项保存失败')
  } finally {
    optionSaving.value = false
  }
}

async function publishCurrent() {
  const kit = editing.value
  const draft = draftRevision(kit)
  if (!kit || !draft) return
  try {
    await ElMessageBox.confirm(
      `发布后将固定为 V${String(draft.versionNumber).padStart(2, '0')}，套件将按唯一明细组展开为真实物料。是否继续？`,
      kit.code ? `发布 ${kit.code} 新版本` : '首次发布并生成套件料号和型号',
      { confirmButtonText: '确认发布', cancelButtonText: '取消', type: 'warning' },
    )
  } catch { return }
  publishing.value = true
  try {
    const saved = await publishEngineeringKit(kit.id, kit.rowVersion, props.token)
    editing.value = saved
    kits.value = kits.value.map(item => item.id === saved.id ? saved : item)
    ElMessage.success(`已发布 ${saved.code} / V${String(draft.versionNumber).padStart(2, '0')}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件发布失败')
  } finally {
    publishing.value = false
  }
}

onMounted(load)
</script>

<template>
  <section class="engineering-kit-library" aria-label="料品套件">
    <template v-if="!editorOpen">
      <div class="engineering-kit-library__toolbar">
        <el-button @click="load">刷新</el-button>
        <el-select v-model="brandFilter" clearable filterable placeholder="筛选品牌"><el-option v-for="brand in brandOptions" :key="brand" :label="brand" :value="brand" /></el-select>
        <el-input v-model="query" clearable placeholder="搜索套件料号、名称、型号或品牌" />
        <div class="engineering-kit-library__actions">
          <el-button v-if="canManage" @click="openOptionMaintenance">代码维护</el-button>
          <el-button v-if="canManage" type="primary" @click="openEditor()">新建套件</el-button>
        </div>
        <span>共 {{ filteredKits.length }} 个套件</span>
      </div>
      <el-table v-loading="loading" :data="filteredKits" row-key="id" height="100%" empty-text="暂无套件">
        <el-table-column label="套件料号" min-width="145"><template #default="{ row }"><strong class="kit-code">{{ row.code || '发布时生成' }}</strong></template></el-table-column>
        <el-table-column prop="name" label="套件名称" min-width="180" />
        <el-table-column label="型号" min-width="145"><template #default="{ row }">{{ row.model || '发布时生成' }}</template></el-table-column>
        <el-table-column prop="brand" label="品牌" min-width="130" />
        <el-table-column prop="description" label="备注" min-width="180" show-overflow-tooltip />
        <el-table-column label="套件内物料数量" width="150"><template #default="{ row }">{{ displayRevision(row)?.components.length ?? 0 }}</template></el-table-column>
        <el-table-column label="状态" width="145"><template #default="{ row }"><el-tag v-if="draftRevision(row)" type="warning" size="small">V{{ String(draftRevision(row)?.versionNumber).padStart(2, '0') }} 草稿</el-tag><el-tag v-else-if="releasedRevision(row)" type="success" size="small">V{{ String(releasedRevision(row)?.versionNumber).padStart(2, '0') }} 已发布</el-tag></template></el-table-column>
        <el-table-column v-if="canManage" label="操作" width="100" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="openEditor(row)">维护</el-button></template></el-table-column>
      </el-table>
    </template>

    <template v-else>
      <header class="engineering-kit-editor__title">
        <div><el-button link type="primary" @click="closeEditor">返回套件列表</el-button><h1>{{ editing ? `${editing.code || '套件草稿'} 套件配置` : '新建套件' }}</h1></div>
        <span>套件主项为虚拟对象，料号由系统在首次发布时生成；型号可手动填写，或按「标准代码-分类代码-序列号」自动生成。</span>
      </header>
      <main class="engineering-kit-editor">
        <section class="engineering-kit-summary">
          <div><label>套件料号</label><strong>{{ editing?.code || '发布时生成' }}</strong></div>
          <div><label>名称</label><el-input v-model="form.name" maxlength="160" placeholder="请输入套件名称" /></div>
          <div class="is-model">
            <div class="engineering-kit-model-head">
              <label>型号</label>
              <el-radio-group v-model="form.modelMode" size="small">
                <el-radio-button label="Auto">自动生成</el-radio-button>
                <el-radio-button label="Manual">手动输入</el-radio-button>
              </el-radio-group>
            </div>
            <el-input v-if="form.modelMode === 'Manual'" v-model="form.model" maxlength="160" placeholder="请输入套件型号" />
            <div v-else class="engineering-kit-model-codes">
              <el-select v-model="form.standardName" filterable clearable placeholder="标准名称"><el-option v-for="item in kitOptions.standardCodes" :key="item.name" :label="`${item.name}（${item.code}）`" :value="item.name" /></el-select>
              <el-select v-model="form.categoryName" filterable clearable placeholder="分类名称"><el-option v-for="item in kitOptions.categoryCodes" :key="item.name" :label="`${item.name}（${item.code}）`" :value="item.name" /></el-select>
            </div>
            <small class="engineering-kit-model-hint">{{ form.modelMode === 'Manual' ? '手动填写：首次发布后固定使用该型号' : `首次发布时生成：${autoModelHint}（当前 ${editing?.model || '未生成'}）` }}</small>
          </div>
          <div><label>品牌</label><el-input v-model="form.brand" maxlength="160" placeholder="请输入品牌" /></div>
          <div><label>备注信息</label><el-input v-model="form.description" maxlength="500" placeholder="备注信息（可选）" /></div>
          <div><label>套件内物料数量</label><strong>{{ form.components.length }}</strong></div>
          <div><label>状态</label><strong>{{ draftRevision(editing) ? `待发布 · V${draftRevision(editing)?.versionNumber}` : releasedRevision(editing) ? '已发布' : '新建草稿' }}</strong></div>
        </section>
        <label class="engineering-kit-change-note"><span>修改说明</span><el-input v-model="form.changeNote" maxlength="500" placeholder="说明本次套件变化" /></label>
        <section class="engineering-kit-group">
          <header class="engineering-kit-group__header">
            <label><span>明细组</span><el-input model-value="套件明细" readonly class="engineering-kit-group-name" /></label>
            <span class="engineering-kit-group__count">共 {{ form.components.length }} 项</span>
            <el-button type="primary" plain @click="addComponent">添加物料</el-button>
          </header>
          <el-table :data="form.components" border empty-text="请添加至少一个已批准真实物料">
            <el-table-column label="料号" min-width="260"><template #default="{ row }"><el-select v-model="row.materialId" filterable placeholder="选择已批准真实物料" style="width:100%"><el-option v-for="material in eligibleMaterials" :key="material.id" :label="materialLabel(material)" :value="material.id" /></el-select></template></el-table-column>
            <el-table-column label="名称" min-width="170"><template #default="{ row }">{{ selectedMaterial(row.materialId)?.name || '—' }}</template></el-table-column>
            <el-table-column label="型号" min-width="170"><template #default="{ row }">{{ selectedMaterial(row.materialId)?.specification || '—' }}</template></el-table-column>
            <el-table-column label="品牌" min-width="120"><template #default="{ row }">{{ selectedMaterial(row.materialId)?.brand || '—' }}</template></el-table-column>
            <el-table-column label="备注" min-width="170" show-overflow-tooltip><template #default="{ row }">{{ selectedMaterial(row.materialId)?.remark || '—' }}</template></el-table-column>
            <el-table-column label="每套数量" width="150"><template #default="{ row }"><el-input-number v-model="row.quantity" :min="0.0001" :precision="4" controls-position="right" style="width:100%" /></template></el-table-column>
            <el-table-column label="操作" width="80"><template #default="{ $index }"><el-button link type="danger" @click="removeComponent($index)">移除</el-button></template></el-table-column>
          </el-table>
        </section>
        <footer class="engineering-kit-editor__footer">
          <el-button @click="closeEditor">返回</el-button>
          <el-button type="primary" :loading="saving" @click="save">保存草稿</el-button>
          <el-button type="success" :disabled="!draftRevision(editing)" :loading="publishing" @click="publishCurrent">发布生效</el-button>
        </footer>
      </main>
    </template>

    <el-dialog v-model="optionDialogOpen" title="套件代码维护" width="min(640px, calc(100vw - 32px))">
      <p class="engineering-kit-option-hint">按“分类名称 + 代码”维护标准代码与分类代码；套件里选择分类名称，型号按「标准代码-分类代码-序列号」生成。</p>
      <el-radio-group v-model="optionSection" class="engineering-kit-option-tabs">
        <el-radio-button label="standardCodes">标准代码</el-radio-button>
        <el-radio-button label="categoryCodes">分类代码</el-radio-button>
      </el-radio-group>
      <div class="engineering-kit-option-list">
        <div v-if="!optionLists.length" class="engineering-kit-option-empty">暂无维护项，请在下方新增。</div>
        <div v-for="(item, index) in optionLists" :key="`${item.name}-${item.code}`" class="engineering-kit-option-row">
          <template v-if="optionEditingIndex === index">
            <el-input v-model="optionEditingName" size="small" placeholder="分类名称" @keyup.enter="commitOptionEdit" />
            <el-input v-model="optionEditingCode" size="small" placeholder="代码" @keyup.enter="commitOptionEdit" />
            <div class="engineering-kit-option-actions"><el-button link type="primary" @click="commitOptionEdit">确定</el-button><el-button link @click="cancelOptionEdit">取消</el-button></div>
          </template>
          <template v-else>
            <strong>{{ item.name }}</strong>
            <code>{{ item.code }}</code>
            <div class="engineering-kit-option-actions">
              <el-button link :disabled="index === 0" @click="moveOption(index, -1)">上移</el-button>
              <el-button link :disabled="index === optionLists.length - 1" @click="moveOption(index, 1)">下移</el-button>
              <el-button link @click="startOptionEdit(index)">编辑</el-button>
              <el-button link type="danger" @click="removeOption(index)">删除</el-button>
            </div>
          </template>
        </div>
        <div class="engineering-kit-option-row is-new">
          <el-input v-model="optionNewName" size="small" placeholder="分类名称" @keyup.enter="addOption" />
          <el-input v-model="optionNewCode" size="small" placeholder="代码" @keyup.enter="addOption" />
          <div class="engineering-kit-option-actions"><el-button type="primary" size="small" :disabled="!optionNewName.trim() || !optionNewCode.trim()" @click="addOption">新增</el-button></div>
        </div>
      </div>
      <template #footer><el-button @click="optionDialogOpen = false">取消</el-button><el-button type="primary" :loading="optionSaving" @click="saveOptionMaintenance">保存</el-button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.engineering-kit-library{display:flex;height:100%;min-height:0;flex-direction:column;overflow:hidden}.engineering-kit-editor__title{display:flex;padding:10px 16px;border-bottom:1px solid #e5eaf1;align-items:center;justify-content:space-between}.engineering-kit-editor__title h1{margin:0;font-size:18px}.engineering-kit-editor__title>span{margin:4px 0 0;color:var(--pdm-muted);font-size:12px}.engineering-kit-library__toolbar{display:flex;flex:0 0 auto;align-items:center;gap:10px;margin-bottom:10px}.engineering-kit-library__toolbar>.el-select{width:150px}.engineering-kit-library__toolbar .el-input{width:min(430px,42vw)}.engineering-kit-library__toolbar span{color:var(--pdm-muted);font-size:12px}.engineering-kit-library>:deep(.el-table){min-height:0;flex:1}.kit-code{color:var(--pdm-green)}.engineering-kit-editor__title>div{display:flex;align-items:center;gap:12px}.engineering-kit-editor{padding:12px 16px;overflow:auto}.engineering-kit-summary{display:grid;overflow:hidden;border:1px solid #dfe6ef;border-radius:8px;grid-template-columns:1.05fr 1.25fr 1.5fr 1.1fr 1.2fr .85fr .9fr}.engineering-kit-library__actions{display:flex;gap:8px}.engineering-kit-summary .is-model{gap:6px}.engineering-kit-model-head{display:flex;align-items:center;gap:8px}.engineering-kit-model-head label{white-space:nowrap}.engineering-kit-model-head :deep(.el-radio-group){flex:0 0 auto}.engineering-kit-model-codes{display:grid;gap:6px;grid-template-columns:1fr 1fr;height:32px}.engineering-kit-model-hint{color:var(--pdm-muted);font-size:11px}.engineering-kit-option-hint{margin:0 0 10px;color:var(--pdm-muted);font-size:12px}.engineering-kit-option-tabs{margin-bottom:10px}.engineering-kit-option-list{display:grid;gap:6px;max-height:52vh;overflow:auto}.engineering-kit-option-row{min-height:34px;display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) auto;align-items:center;gap:8px;border:1px solid var(--pdm-border);border-radius:6px;padding:5px 9px}.engineering-kit-option-row code{color:var(--pdm-muted);font-size:12px}.engineering-kit-option-row strong{min-width:0;overflow:hidden;font-size:12px;text-overflow:ellipsis;white-space:nowrap}.engineering-kit-option-row.is-new{border-style:dashed}.engineering-kit-option-actions{display:flex;align-items:center;gap:4px}.engineering-kit-option-empty{padding:12px;color:var(--pdm-muted);font-size:12px;text-align:center}.engineering-kit-summary>div{display:flex;min-height:68px;padding:9px 12px;border-right:1px solid #dfe6ef;flex-direction:column;justify-content:center;gap:8px}.engineering-kit-summary>div:last-child{border-right:0}.engineering-kit-summary label,.engineering-kit-change-note>span,.engineering-kit-group__header label>span{color:var(--pdm-text-soft);font-size:12px;font-weight:600}.engineering-kit-summary strong{display:flex;min-height:32px;align-items:center}.engineering-kit-change-note{display:grid;margin:12px 0;align-items:center;grid-template-columns:82px 1fr}.engineering-kit-group{padding:12px;border:1px solid #cbdced;border-radius:8px;background:#f7fbff}.engineering-kit-group :deep(.el-table__header .cell),.engineering-kit-group :deep(.el-table__body .cell){text-align:center;justify-content:center}.engineering-kit-group__header{display:flex;margin-bottom:10px;align-items:center;gap:12px}.engineering-kit-group__header label{display:flex;align-items:center;gap:10px}.engineering-kit-group-name{width:200px}.engineering-kit-group__count{color:var(--pdm-muted);font-size:12px}.engineering-kit-group__header>.el-button{margin-left:auto}.engineering-kit-editor__footer{display:flex;margin-top:14px;justify-content:flex-end;gap:8px}@media(max-width:1000px){.engineering-kit-library__toolbar{flex-wrap:wrap}.engineering-kit-summary{grid-template-columns:repeat(2,1fr)}.engineering-kit-summary>div{border-bottom:1px solid #dfe6ef}.engineering-kit-editor__title{align-items:flex-start;flex-direction:column;gap:6px}}
</style>
