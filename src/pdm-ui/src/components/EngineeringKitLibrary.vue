<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, onMounted, ref } from 'vue'
import { listEngineeringKits, listMaterials, publishEngineeringKit, saveEngineeringKit } from '../api'
import type { EngineeringKit, EngineeringKitComponent, PdmMaterial } from '../types'

const props = defineProps<{ token: string; canManage?: boolean }>()
type ComponentDraft = { materialId: string; quantity: number; isOptional: boolean }

const loading = ref(false)
const saving = ref(false)
const publishingId = ref('')
const query = ref('')
const kits = ref<EngineeringKit[]>([])
const materials = ref<PdmMaterial[]>([])
const editorOpen = ref(false)
const editing = ref<EngineeringKit | null>(null)
const form = ref({ name: '', description: '', changeNote: '', components: [] as ComponentDraft[] })

const filteredKits = computed(() => {
  const value = query.value.trim().toLocaleLowerCase()
  if (!value) return kits.value
  return kits.value.filter(item => `${item.code ?? ''} ${item.name} ${item.description ?? ''}`.toLocaleLowerCase().includes(value))
})
const eligibleMaterials = computed(() => materials.value.filter(item => !item.isArchived && item.approvalStatus === 'Approved'))

function draftRevision(kit?: EngineeringKit) {
  return kit ? [...kit.revisions].filter(item => item.state === 'Draft').sort((a, b) => b.versionNumber - a.versionNumber)[0] : undefined
}

function releasedRevision(kit?: EngineeringKit) {
  return kit?.revisions.find(item => item.id === kit.currentReleasedRevisionId)
}

function displayRevision(kit: EngineeringKit) {
  return draftRevision(kit) ?? releasedRevision(kit)
}

function materialLabel(materialId: string) {
  const material = eligibleMaterials.value.find(item => item.id === materialId)
  return material ? `${material.materialCode} · ${material.name}` : materialId
}

function formatDate(value?: string) {
  return value ? new Date(value).toLocaleDateString('zh-CN') : '—'
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
    description: kit?.description ?? '',
    changeNote: draftRevision(kit)?.changeNote ?? '',
    components: revision?.components.map(item => ({ materialId: item.materialId, quantity: item.quantity, isOptional: item.isOptional })) ?? [],
  }
  editorOpen.value = true
}

function addComponent() {
  form.value.components.push({ materialId: '', quantity: 1, isOptional: false })
}

function removeComponent(index: number) {
  form.value.components.splice(index, 1)
}

async function save() {
  if (!form.value.name.trim()) return ElMessage.warning('请填写套件名称')
  if (!form.value.components.some(item => !item.isOptional)) return ElMessage.warning('套件至少需要一个必选子料')
  if (form.value.components.some(item => !item.materialId || item.quantity <= 0)) return ElMessage.warning('请完整填写子料和数量')
  if (new Set(form.value.components.map(item => item.materialId)).size !== form.value.components.length) return ElMessage.warning('同一真实物料只能出现一次')
  saving.value = true
  try {
    await saveEngineeringKit({
      name: form.value.name.trim(),
      description: form.value.description.trim(),
      changeNote: form.value.changeNote.trim(),
      components: form.value.components.map((item, index) => ({ ...item, sortOrder: index + 1 })),
      expectedRowVersion: editing.value?.rowVersion,
    }, props.token, editing.value?.id)
    editorOpen.value = false
    ElMessage.success('套件草稿已保存')
    await load()
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件保存失败')
  } finally {
    saving.value = false
  }
}

async function publishKit(kit: EngineeringKit) {
  const draft = draftRevision(kit)
  if (!draft) return
  try {
    await ElMessageBox.confirm(
      `发布后将固定为 V${String(draft.versionNumber).padStart(2, '0')}，已被项目引用的旧版本不会变化。是否继续？`,
      kit.code ? `发布 ${kit.code} 新版本` : '首次发布并生成 UKIT 编码',
      { confirmButtonText: '确认发布', cancelButtonText: '取消', type: 'warning' },
    )
  } catch { return }
  publishingId.value = kit.id
  try {
    const saved = await publishEngineeringKit(kit.id, kit.rowVersion, props.token)
    ElMessage.success(`已发布 ${saved.code} / V${String(draft.versionNumber).padStart(2, '0')}`)
    await load()
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件发布失败')
  } finally {
    publishingId.value = ''
  }
}

onMounted(load)
</script>

<template>
  <section class="engineering-kit-library pdm-panel" aria-label="标准结构套件库">
    <header class="engineering-kit-library__header">
      <div><h1>标准结构</h1><p>UKIT 套件仅供 PDM 工程引用，套件本身不进入 U9C；发布到 BOM 时自动展开为真实料品。</p></div>
      <el-button v-if="canManage" type="primary" @click="openEditor()">新建套件</el-button>
    </header>
    <div class="engineering-kit-library__toolbar">
      <el-input v-model="query" clearable placeholder="搜索 UKIT 编码、名称或说明" />
      <span>共 {{ filteredKits.length }} 个套件</span>
    </div>
    <el-table v-loading="loading" :data="filteredKits" row-key="id" height="100%" empty-text="暂无套件">
      <el-table-column type="expand" width="40">
        <template #default="{ row }">
          <div class="engineering-kit-components">
            <strong>{{ row.code || '待发布' }} · {{ row.name }}</strong>
            <el-table :data="displayRevision(row)?.components ?? []" size="small" border>
              <el-table-column prop="materialCode" label="真实料号" width="150" />
              <el-table-column prop="materialName" label="物料名称" min-width="180" />
              <el-table-column prop="quantity" label="每套数量" width="100" />
              <el-table-column prop="unit" label="单位" width="80" />
              <el-table-column label="类型" width="90"><template #default="scope"><el-tag :type="scope.row.isOptional ? 'warning' : 'success'" size="small">{{ scope.row.isOptional ? '可选' : '必选' }}</el-tag></template></el-table-column>
            </el-table>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="套件编码" width="150"><template #default="{ row }"><strong class="kit-code">{{ row.code || '发布时生成' }}</strong></template></el-table-column>
      <el-table-column prop="name" label="套件名称" min-width="180" />
      <el-table-column prop="description" label="说明" min-width="220" show-overflow-tooltip />
      <el-table-column label="当前状态" width="150"><template #default="{ row }"><span v-if="draftRevision(row)"><el-tag type="warning" size="small">V{{ String(draftRevision(row)?.versionNumber).padStart(2, '0') }} 草稿</el-tag></span><span v-else-if="releasedRevision(row)"><el-tag type="success" size="small">V{{ String(releasedRevision(row)?.versionNumber).padStart(2, '0') }} 已发布</el-tag></span></template></el-table-column>
      <el-table-column label="子料" width="110"><template #default="{ row }">必选 {{ displayRevision(row)?.components.filter((item: EngineeringKitComponent) => !item.isOptional).length ?? 0 }} · 可选 {{ displayRevision(row)?.components.filter((item: EngineeringKitComponent) => item.isOptional).length ?? 0 }}</template></el-table-column>
      <el-table-column label="更新时间" width="120"><template #default="{ row }">{{ formatDate(row.updatedAt) }}</template></el-table-column>
      <el-table-column v-if="canManage" label="操作" width="150" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="openEditor(row)">编辑</el-button><el-button v-if="draftRevision(row)" link type="success" :loading="publishingId === row.id" @click="publishKit(row)">发布</el-button></template></el-table-column>
    </el-table>

    <el-dialog v-model="editorOpen" :title="editing ? `编辑 ${editing.code || '套件草稿'}` : '新建套件'" width="min(900px, calc(100vw - 32px))">
      <el-form label-position="top">
        <div class="engineering-kit-form-grid">
          <el-form-item label="套件名称" required><el-input v-model="form.name" maxlength="160" /></el-form-item>
          <el-form-item label="变更说明"><el-input v-model="form.changeNote" maxlength="500" placeholder="新版本建议填写" /></el-form-item>
        </div>
        <el-form-item label="套件说明"><el-input v-model="form.description" type="textarea" :rows="2" maxlength="500" /></el-form-item>
        <div class="engineering-kit-component-title"><div><strong>真实子料</strong><span>必选项不可在项目 BOM 中单独删除；可选项默认不选。</span></div><el-button @click="addComponent">添加子料</el-button></div>
        <el-table :data="form.components" border max-height="380" empty-text="请添加至少一个必选子料">
          <el-table-column label="料品主档" min-width="360"><template #default="{ row }"><el-select v-model="row.materialId" filterable placeholder="选择已批准真实料品" style="width:100%"><el-option v-for="material in eligibleMaterials" :key="material.id" :label="materialLabel(material.id)" :value="material.id" /></el-select></template></el-table-column>
          <el-table-column label="每套数量" width="150"><template #default="{ row }"><el-input-number v-model="row.quantity" :min="0.0001" :precision="4" controls-position="right" style="width:100%" /></template></el-table-column>
          <el-table-column label="子料类型" width="150"><template #default="{ row }"><el-switch v-model="row.isOptional" active-text="可选" inactive-text="必选" /></template></el-table-column>
          <el-table-column label="操作" width="80"><template #default="{ $index }"><el-button link type="danger" @click="removeComponent($index)">删除</el-button></template></el-table-column>
        </el-table>
      </el-form>
      <template #footer><el-button @click="editorOpen = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存草稿</el-button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.engineering-kit-library{display:flex;height:calc(100vh - 104px);min-height:560px;flex-direction:column;overflow:hidden}.engineering-kit-library__header{display:flex;padding:16px 20px;border-bottom:1px solid #e5eaf1;align-items:center;justify-content:space-between}.engineering-kit-library__header h1{margin:0;font-size:18px}.engineering-kit-library__header p{margin:4px 0 0;color:#64748b;font-size:12px}.engineering-kit-library__toolbar{display:flex;padding:12px 16px;align-items:center;gap:12px}.engineering-kit-library__toolbar .el-input{max-width:360px}.engineering-kit-library__toolbar span{color:#64748b;font-size:12px}.engineering-kit-library>:deep(.el-table){min-height:0;flex:1}.engineering-kit-components{padding:12px 48px}.engineering-kit-components>strong{display:block;margin-bottom:10px}.kit-code{color:#0f766e}.engineering-kit-form-grid{display:grid;grid-template-columns:1fr 1fr;gap:12px}.engineering-kit-component-title{display:flex;margin:8px 0 10px;align-items:center;justify-content:space-between}.engineering-kit-component-title div{display:flex;flex-direction:column;gap:3px}.engineering-kit-component-title span{color:#64748b;font-size:12px}@media(max-width:760px){.engineering-kit-library__header{align-items:flex-start;gap:12px}.engineering-kit-form-grid{grid-template-columns:1fr}}
</style>
