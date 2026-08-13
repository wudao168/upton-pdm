<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { ChevronDown, ChevronRight, FolderKanban, FolderPlus, Plus } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import type { CreateProjectInput, CreateSubprojectInput, PdmCustomer, PdmUser, ProjectNumberingOptions, ProjectSummary } from '../types'

const props = defineProps<{
  projects: ProjectSummary[]
  numberingOptions: ProjectNumberingOptions
  customers: PdmCustomer[]
  users: PdmUser[]
  currentProjectId: string
  canCreate: boolean
  canManage: boolean
  pending: boolean
  onCreate: (input: CreateProjectInput) => Promise<ProjectSummary>
  onCreateSubproject: (parentProjectId: string, input: CreateSubprojectInput) => Promise<ProjectSummary>
  onUpdateCounters: (organizationId: string, currentProjectSequence: number, currentSerialSequence: number) => Promise<ProjectNumberingOptions>
  onUpdateResponsibles: (projectId: string, usernames: string[]) => Promise<ProjectSummary>
}>()

const emit = defineEmits<{ select: [projectId: string]; open: [projectId: string] }>()
const dialogOpen = ref(false)
const childDialogOpen = ref(false)
const childParent = ref<ProjectSummary | null>(null)
const counterDialogOpen = ref(false)
const responsibleDialogOpen = ref(false)
const responsibleProject = ref<ProjectSummary | null>(null)
const responsibleDraft = ref<string[]>([])
const counterDrafts = ref<Array<{ id: string; name: string; project: number; serial: number }>>([])
const expanded = ref(new Set<string>())
const form = reactive<CreateProjectInput>({
  organizationId: '', projectTypeCode: '', equipmentTypeCode: 0, customerId: '', name: '', projectAlias: '', signedDate: '', quantity: 1,
})
const childForm = reactive<CreateSubprojectInput>({ name: '', projectAlias: '', quantity: 1 })

const rootProjects = computed(() => props.projects.filter(item => !item.parentProjectId))
const activeCustomers = computed(() => props.customers.filter(item => item.isActive))
const selectedCustomer = computed(() => props.customers.find(item => item.id === form.customerId))
const childrenByParent = computed(() => {
  const result = new Map<string, ProjectSummary[]>()
  for (const project of props.projects.filter(item => item.parentProjectId)) {
    const children = result.get(project.parentProjectId!) ?? []
    children.push(project)
    result.set(project.parentProjectId!, children)
  }
  for (const children of result.values()) children.sort((left, right) => (left.childSequence ?? 0) - (right.childSequence ?? 0))
  return result
})
watch(() => rootProjects.value.map(item => item.id).join(','), () => {
  expanded.value = new Set(rootProjects.value.filter(item => childrenByParent.value.has(item.id)).map(item => item.id))
}, { immediate: true })

function localDate() {
  const date = new Date()
  const offset = date.getTimezoneOffset() * 60_000
  return new Date(date.getTime() - offset).toISOString().slice(0, 10)
}

function openCreateDialog() {
  form.organizationId = props.numberingOptions.organizations[0]?.id ?? ''
  form.projectTypeCode = props.numberingOptions.projectTypes[0]?.code ?? ''
  form.equipmentTypeCode = props.numberingOptions.equipmentTypes[0]?.code ?? 0
  form.customerId = activeCustomers.value[0]?.id ?? ''
  form.name = ''
  form.projectAlias = ''
  form.signedDate = localDate()
  form.quantity = 1
  dialogOpen.value = true
}

function openResponsibleDialog(project: ProjectSummary) {
  responsibleProject.value = project
  responsibleDraft.value = [...project.responsibleUsers]
  responsibleDialogOpen.value = true
}

async function saveResponsibles() {
  if (!responsibleProject.value || responsibleDraft.value.length === 0) {
    ElMessage.warning('请至少选择一个负责人账号')
    return
  }
  try {
    await props.onUpdateResponsibles(responsibleProject.value.id, responsibleDraft.value)
    responsibleDialogOpen.value = false
    ElMessage.success('项目负责人已更新')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '负责人更新失败')
  }
}

function openChildDialog(parent: ProjectSummary) {
  childParent.value = parent
  childForm.name = ''
  childForm.projectAlias = ''
  childForm.quantity = 1
  childDialogOpen.value = true
}

function openCounterDialog() {
  counterDrafts.value = props.numberingOptions.organizations.map(item => ({ id: item.id, name: item.name, project: item.currentProjectSequence, serial: item.currentSerialSequence }))
  counterDialogOpen.value = true
}

async function saveCounters(row: { id: string; project: number; serial: number }) {
  try {
    await props.onUpdateCounters(row.id, row.project, row.serial)
    ElMessage.success('流水基线已更新，新项目将从下一号开始')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '流水基线更新失败')
  }
}

function toggle(projectId: string) {
  const next = new Set(expanded.value)
  if (next.has(projectId)) next.delete(projectId)
  else next.add(projectId)
  expanded.value = next
}

async function submitProject() {
  if (!form.organizationId || !form.projectTypeCode || !form.customerId || !form.name.trim() || !form.signedDate || form.quantity < 1) {
    ElMessage.warning('请完整填写项目资料并选择客户')
    return
  }
  try {
    await props.onCreate({ ...form, name: form.name.trim(), projectAlias: form.projectAlias?.trim() })
    dialogOpen.value = false
    ElMessage.success('项目号、设备型号和序列号已自动生成')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '项目创建失败')
  }
}

async function submitSubproject() {
  if (!childParent.value || !childForm.name.trim() || childForm.quantity < 1) {
    ElMessage.warning('请填写子项目名称和数量')
    return
  }
  try {
    const parentId = childParent.value.id
    await props.onCreateSubproject(parentId, { name: childForm.name.trim(), projectAlias: childForm.projectAlias?.trim(), quantity: childForm.quantity })
    expanded.value = new Set([...expanded.value, parentId])
    childDialogOpen.value = false
    ElMessage.success('子项目号、设备型号和序列号已自动生成')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '子项目创建失败')
  }
}
</script>

<template>
  <section class="pdm-project-manager" aria-label="项目管理">
    <header class="pdm-pagebar">
      <div>
        <div class="pdm-breadcrumb">项目管理 <span>/</span> 项目中心</div>
        <h1>项目中心</h1>
        <p>项目号、设备型号和序列号由系统统一生成；一个项目号对应一个设备型号，数量决定序列号数量。</p>
      </div>
      <div class="pdm-page-actions">
        <button v-if="canManage" type="button" class="pdm-secondary-action" @click="openCounterDialog">流水基线设置</button>
        <button v-if="canCreate" type="button" class="pdm-primary-action" @click="openCreateDialog"><FolderPlus :size="16" />创建主项目</button>
        <small v-else class="pdm-project-permission-note">当前角色仅可查看负责的项目</small>
      </div>
    </header>

    <div class="pdm-project-summary">
      <article class="pdm-panel"><span>主项目</span><strong>{{ rootProjects.length }}</strong></article>
      <article class="pdm-panel"><span>子项目</span><strong>{{ projects.length - rootProjects.length }}</strong></article>
      <article class="pdm-panel"><span>当前项目</span><strong>{{ projects.find(item => item.id === currentProjectId)?.code || '未选择' }}</strong></article>
    </div>

    <section class="pdm-panel pdm-project-list">
      <header class="pdm-panel-heading"><h2>项目列表</h2><small>管理员查看全部；其他角色仅查看负责人账号为本人的项目</small></header>
      <div v-if="rootProjects.length" class="pdm-table-scroll">
        <table class="pdm-project-table pdm-project-number-table">
          <thead><tr><th>项目号</th><th>项目名称 / 别名</th><th>设备型号</th><th>序列号</th><th>数量</th><th>客户</th><th>负责人</th><th>操作</th></tr></thead>
          <tbody v-for="parent in rootProjects" :key="parent.id">
            <tr :class="{ 'is-current': parent.id === currentProjectId }">
              <td><button v-if="childrenByParent.has(parent.id)" type="button" class="pdm-tree-toggle" @click="toggle(parent.id)"><ChevronDown v-if="expanded.has(parent.id)" :size="15" /><ChevronRight v-else :size="15" /></button><strong>{{ parent.code }}</strong><em v-if="parent.id === currentProjectId">当前</em></td>
              <td>{{ parent.name }}<small v-if="parent.projectAlias">{{ parent.projectAlias }}</small></td>
              <td>{{ parent.deviceModel || '旧项目未编号' }}</td>
              <td><span v-for="serial in parent.serialNumbers" :key="serial" class="pdm-serial-line">{{ serial }}</span></td>
              <td>{{ parent.quantity }}</td>
              <td>{{ parent.customerName || '—' }}<small v-if="parent.customerCode">{{ parent.customerCode }}</small></td>
              <td><span v-for="username in parent.responsibleUsers" :key="username" class="pdm-serial-line">{{ username }}</span><button v-if="canManage" type="button" class="pdm-text-action" @click="openResponsibleDialog(parent)">维护</button></td>
              <td><button type="button" class="pdm-text-action" @click="emit('select', parent.id)">设为当前</button><button type="button" class="pdm-text-action" @click="emit('open', parent.id)">进入项目</button><button v-if="canCreate && parent.deviceModel" type="button" class="pdm-text-action" @click="openChildDialog(parent)"><Plus :size="13" />子项目</button></td>
            </tr>
            <tr v-for="child in (expanded.has(parent.id) ? childrenByParent.get(parent.id) ?? [] : [])" :key="child.id" class="is-child" :class="{ 'is-current': child.id === currentProjectId }">
              <td><span class="pdm-child-indent"></span><strong>{{ child.code }}</strong><em v-if="child.id === currentProjectId">当前</em></td>
              <td>{{ child.name }}<small v-if="child.projectAlias">{{ child.projectAlias }}</small></td>
              <td>{{ child.deviceModel }}</td>
              <td><span v-for="serial in child.serialNumbers" :key="serial" class="pdm-serial-line">{{ serial }}</span></td>
              <td>{{ child.quantity }}</td><td>{{ child.customerName }}<small>{{ child.customerCode }}</small></td>
              <td><span v-for="username in child.responsibleUsers" :key="username" class="pdm-serial-line">{{ username }}</span><button v-if="canManage" type="button" class="pdm-text-action" @click="openResponsibleDialog(child)">维护</button></td>
              <td><button type="button" class="pdm-text-action" @click="emit('select', child.id)">设为当前</button><button type="button" class="pdm-text-action" @click="emit('open', child.id)">进入项目</button></td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else class="pdm-project-empty"><FolderKanban :size="42" /><h2>当前账号还没有负责的项目</h2><p v-if="canCreate">创建后，在SolidWorks端刷新项目列表并选择项目，即可关联图纸。</p><p v-else>请联系管理员将项目负责人账号设置为您的PDM登录账号。</p></div>
    </section>

    <el-dialog v-model="dialogOpen" title="创建主项目" width="680px" :close-on-click-modal="false">
      <form class="pdm-project-form" aria-label="创建PDM项目" @submit.prevent="submitProject">
        <label>所属公司<select v-model="form.organizationId" name="organizationId"><option v-for="item in numberingOptions.organizations" :key="item.id" :value="item.id">{{ item.name }}（{{ item.projectCompanyCode }} / {{ item.modelCompanyCode }}）</option></select></label>
        <label>项目类型<select v-model="form.projectTypeCode" name="projectTypeCode"><option v-for="item in numberingOptions.projectTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
        <label>设备类型<select v-model.number="form.equipmentTypeCode" name="equipmentTypeCode"><option v-for="item in numberingOptions.equipmentTypes" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
        <label class="is-wide">客户<select v-model="form.customerId" name="customerId"><option value="" disabled>请选择客户</option><option v-for="item in activeCustomers" :key="item.id" :value="item.id">{{ item.name }}（{{ item.code }}）</option></select><small v-if="selectedCustomer">客户编码由客户档案自动带出：{{ selectedCustomer.code }}</small></label>
        <label>项目名称<input v-model="form.name" name="projectName" maxlength="200" placeholder="人工录入"></label>
        <label>项目别名<input v-model="form.projectAlias" name="projectAlias" maxlength="200" placeholder="人工录入，可选"></label>
        <label>签订日期<input v-model="form.signedDate" name="signedDate" type="date"></label>
        <label>数量<input v-model.number="form.quantity" name="quantity" type="number" min="1" max="10000"></label>
        <p class="is-wide">项目号、客户编码、客户项目流水号和序列号由服务器生成；存档目录按“系统设置根目录\项目号”自动创建。负责人请在项目列表中维护，可多选。</p>
      </form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="dialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submitProject">{{ pending ? '正在创建…' : '创建并取号' }}</button></template>
    </el-dialog>

    <el-dialog v-model="responsibleDialogOpen" :title="`维护负责人 · ${responsibleProject?.code ?? ''}`" width="560px" :close-on-click-modal="false">
      <label class="pdm-dialog-field">负责人账号<el-select v-model="responsibleDraft" multiple filterable placeholder="请选择一个或多个账号" style="width:100%"><el-option v-for="user in users.filter(item => item.isActive)" :key="user.username" :label="`${user.displayName}（${user.username}）`" :value="user.username" /></el-select></label>
      <p class="pdm-counter-note">被选账号可以看到并进入该项目；负责人只能在项目管理页面调整。</p>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="responsibleDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveResponsibles">保存负责人</button></template>
    </el-dialog>

    <el-dialog v-model="childDialogOpen" :title="`创建子项目 · ${childParent?.code ?? ''}`" width="560px" :close-on-click-modal="false">
      <form class="pdm-project-form" aria-label="创建PDM子项目" @submit.prevent="submitSubproject">
        <label>子项目名称<input v-model="childForm.name" name="childProjectName" maxlength="200" placeholder="人工录入"></label>
        <label>子项目别名<input v-model="childForm.projectAlias" name="childProjectAlias" maxlength="200" placeholder="人工录入，可选"></label>
        <label>数量<input v-model.number="childForm.quantity" name="childQuantity" type="number" min="1" max="10000"></label>
        <p class="is-wide">子项目号按主项目号追加-1、-2…；一个子项目号只生成一个设备型号，数量仅分配对应数量的连续序列号。</p>
      </form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="childDialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submitSubproject">{{ pending ? '正在创建…' : '创建子项目' }}</button></template>
    </el-dialog>

    <el-dialog v-model="counterDialogOpen" title="组织流水基线设置" width="720px" :close-on-click-modal="false">
      <p class="pdm-counter-note">填写各组织已使用的最大流水值。系统只允许向前调整；例如昆山现有项目P702130、序列号70006071时，应分别填写2130和6071。</p>
      <table class="pdm-project-table pdm-counter-table">
        <thead><tr><th>组织</th><th>当前项目流水</th><th>当前序列流水</th><th>操作</th></tr></thead>
        <tbody><tr v-for="row in counterDrafts" :key="row.id"><td>{{ row.name }}</td><td><input v-model.number="row.project" type="number" min="0" max="99999"></td><td><input v-model.number="row.serial" type="number" min="0" max="9999999"></td><td><button type="button" class="pdm-text-action" :disabled="pending" @click="saveCounters(row)">保存此组织</button></td></tr></tbody>
      </table>
      <template #footer><button type="button" class="pdm-primary-action" @click="counterDialogOpen=false">完成</button></template>
    </el-dialog>
  </section>
</template>
