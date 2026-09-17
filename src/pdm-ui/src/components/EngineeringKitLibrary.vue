<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { listEngineeringKits, listMaterials, publishEngineeringKit, saveEngineeringKit } from '../api'
import type { EngineeringKit, PdmMaterial } from '../types'

const props = defineProps<{ token: string; canManage: boolean }>()
const loading = ref(false)
const saving = ref(false)
const publishing = ref(false)
const query = ref('')
const kits = ref<EngineeringKit[]>([])
const materials = ref<PdmMaterial[]>([])
const editorOpen = ref(false)
const editing = ref<EngineeringKit | null>(null)
const form = ref({ name: '', brand: '', changeNote: '', components: [] as Array<{ materialId: string; quantity: number; isOptional: false }> })

const eligibleMaterials = computed(() => materials.value.filter(item => !item.isArchived && item.approvalStatus === 'Approved'))
const filteredKits = computed(() => {
  const keyword = query.value.trim().toLowerCase()
  if (!keyword) return kits.value
  return kits.value.filter(item => [item.code, item.model, item.name, item.brand].some(value => value?.toLowerCase().includes(keyword)))
})

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
    const [loadedKits, loadedMaterials] = await Promise.all([
      listEngineeringKits(props.token, !props.canManage),
      props.canManage ? listMaterials(props.token, '', false, 500) : Promise.resolve([]),
    ])
    kits.value = loadedKits
    materials.value = loadedMaterials
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件加载失败')
  } finally {
    loading.value = false
  }
}

function openEditor(kit?: EngineeringKit) {
  editing.value = kit ?? null
  const revision = kit ? displayRevision(kit) : undefined
  form.value = {
    name: kit?.name ?? '',
    brand: kit?.brand ?? '',
    changeNote: draftRevision(kit)?.changeNote ?? '',
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
  if (form.value.components.length === 0) return ElMessage.warning('套件至少需要一个明细物料')
  if (form.value.components.some(item => !item.materialId || item.quantity <= 0)) return ElMessage.warning('请完整填写物料和每套数量')
  if (new Set(form.value.components.map(item => item.materialId)).size !== form.value.components.length) return ElMessage.warning('同一真实物料只能出现一次')
  saving.value = true
  try {
    const saved = await saveEngineeringKit({
      name: form.value.name.trim(),
      brand: form.value.brand.trim(),
      changeNote: form.value.changeNote.trim(),
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
      <header class="engineering-kit-library__header">
        <div><h1>料品套件</h1><p>套件是 PDM 内的虚拟组合，不进入库存、采购或 U9C；发布到 BOM 时展开为真实料品。</p></div>
        <el-button v-if="canManage" type="primary" @click="openEditor()">新建套件</el-button>
      </header>
      <div class="engineering-kit-library__toolbar">
        <el-button @click="load">刷新</el-button>
        <el-input v-model="query" clearable placeholder="搜索套件料号、名称、型号或品牌" />
        <span>共 {{ filteredKits.length }} 个套件</span>
      </div>
      <el-table v-loading="loading" :data="filteredKits" row-key="id" height="100%" empty-text="暂无套件">
        <el-table-column label="套件料号" min-width="145"><template #default="{ row }"><strong class="kit-code">{{ row.code || '发布时生成' }}</strong></template></el-table-column>
        <el-table-column prop="name" label="套件名称" min-width="180" />
        <el-table-column label="型号" min-width="145"><template #default="{ row }">{{ row.model || '发布时生成' }}</template></el-table-column>
        <el-table-column prop="brand" label="品牌" min-width="130" />
        <el-table-column label="套件内物料数量" width="150"><template #default="{ row }">{{ displayRevision(row)?.components.length ?? 0 }}</template></el-table-column>
        <el-table-column label="状态" width="145"><template #default="{ row }"><el-tag v-if="draftRevision(row)" type="warning" size="small">V{{ String(draftRevision(row)?.versionNumber).padStart(2, '0') }} 草稿</el-tag><el-tag v-else-if="releasedRevision(row)" type="success" size="small">V{{ String(releasedRevision(row)?.versionNumber).padStart(2, '0') }} 已发布</el-tag></template></el-table-column>
        <el-table-column v-if="canManage" label="操作" width="100" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="openEditor(row)">维护</el-button></template></el-table-column>
      </el-table>
    </template>

    <template v-else>
      <header class="engineering-kit-editor__title">
        <div><el-button link type="primary" @click="closeEditor">返回套件列表</el-button><h1>{{ editing ? `${editing.code || '套件草稿'} 套件配置` : '新建套件' }}</h1></div>
        <span>套件主项为虚拟对象，料号和型号由系统在首次发布时生成。</span>
      </header>
      <main class="engineering-kit-editor">
        <section class="engineering-kit-summary">
          <div><label>套件料号</label><strong>{{ editing?.code || '发布时生成' }}</strong></div>
          <div><label>名称</label><el-input v-model="form.name" maxlength="160" placeholder="请输入套件名称" /></div>
          <div><label>型号</label><strong>{{ editing?.model || '发布时生成' }}</strong></div>
          <div><label>品牌</label><el-input v-model="form.brand" maxlength="160" placeholder="请输入品牌" /></div>
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
  </section>
</template>

<style scoped>
.engineering-kit-library{display:flex;height:100%;min-height:0;flex-direction:column;overflow:hidden}.engineering-kit-library__header,.engineering-kit-editor__title{display:flex;padding:10px 16px;border-bottom:1px solid #e5eaf1;align-items:center;justify-content:space-between}.engineering-kit-library__header h1,.engineering-kit-editor__title h1{margin:0;font-size:18px}.engineering-kit-library__header p,.engineering-kit-editor__title>span{margin:4px 0 0;color:#64748b;font-size:12px}.engineering-kit-library__toolbar{display:flex;padding:10px 16px;align-items:center;gap:12px}.engineering-kit-library__toolbar .el-input{max-width:420px}.engineering-kit-library__toolbar span{color:#64748b;font-size:12px}.engineering-kit-library>:deep(.el-table){min-height:0;flex:1}.kit-code{color:#0f766e}.engineering-kit-editor__title>div{display:flex;align-items:center;gap:12px}.engineering-kit-editor{padding:12px 16px;overflow:auto}.engineering-kit-summary{display:grid;overflow:hidden;border:1px solid #dfe6ef;border-radius:8px;grid-template-columns:1.05fr 1.45fr 1.05fr 1.25fr 1fr 1fr}.engineering-kit-summary>div{display:flex;min-height:68px;padding:9px 12px;border-right:1px solid #dfe6ef;flex-direction:column;justify-content:center;gap:8px}.engineering-kit-summary>div:last-child{border-right:0}.engineering-kit-summary label,.engineering-kit-change-note>span,.engineering-kit-group__header label>span{color:#475569;font-size:12px;font-weight:600}.engineering-kit-summary strong{display:flex;min-height:32px;align-items:center}.engineering-kit-change-note{display:grid;margin:12px 0;align-items:center;grid-template-columns:82px 1fr}.engineering-kit-group{padding:12px;border:1px solid #cbdced;border-radius:8px;background:#f7fbff}.engineering-kit-group__header{display:flex;margin-bottom:10px;align-items:center;gap:12px}.engineering-kit-group__header label{display:flex;align-items:center;gap:10px}.engineering-kit-group-name{width:200px}.engineering-kit-group__count{color:#64748b;font-size:12px}.engineering-kit-group__header>.el-button{margin-left:auto}.engineering-kit-editor__footer{display:flex;margin-top:14px;justify-content:flex-end;gap:8px}@media(max-width:1000px){.engineering-kit-summary{grid-template-columns:repeat(2,1fr)}.engineering-kit-summary>div{border-bottom:1px solid #dfe6ef}.engineering-kit-editor__title{align-items:flex-start;flex-direction:column;gap:6px}}
</style>
