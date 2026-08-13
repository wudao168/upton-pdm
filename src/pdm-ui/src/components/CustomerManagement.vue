<script setup lang="ts">
import { Plus, Search } from '@lucide/vue'
import { computed, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import type { PdmCustomer } from '../types'

const props = defineProps<{
  customers: PdmCustomer[]
  pending: boolean
  onSave: (customer: Partial<PdmCustomer> & Pick<PdmCustomer, 'code' | 'name' | 'isActive'>) => Promise<PdmCustomer>
}>()

const query = ref('')
const dialogOpen = ref(false)
const draft = reactive<{ id?: string; code: string; name: string; isActive: boolean }>({ code: '', name: '', isActive: true })
const filteredCustomers = computed(() => {
  const keyword = query.value.trim().toLocaleLowerCase('zh-CN')
  return keyword ? props.customers.filter(item => `${item.code} ${item.name}`.toLocaleLowerCase('zh-CN').includes(keyword)) : props.customers
})

function openCreate() {
  draft.id = undefined
  draft.code = ''
  draft.name = ''
  draft.isActive = true
  dialogOpen.value = true
}

function openEdit(customer: PdmCustomer) {
  Object.assign(draft, customer)
  dialogOpen.value = true
}

async function submit() {
  if (!draft.code.trim() || !draft.name.trim()) {
    ElMessage.warning('请填写客户编码和客户名称')
    return
  }
  try {
    await props.onSave({ id: draft.id, code: draft.code.trim().toUpperCase(), name: draft.name.trim(), isActive: draft.isActive })
    dialogOpen.value = false
    ElMessage.success(draft.id ? '客户档案已更新' : '客户档案已创建')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '客户保存失败')
  }
}
</script>

<template>
  <section class="pdm-project-manager" aria-label="客户维护">
    <header class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">基础资料 <span>/</span> 客户维护</div><h1>客户维护</h1><p>创建项目时只选择客户，客户编码由这里维护的客户档案自动带出。</p></div>
      <button type="button" class="pdm-primary-action" @click="openCreate"><Plus :size="16" />新增客户</button>
    </header>
    <section class="pdm-panel pdm-project-list">
      <header class="pdm-panel-heading"><h2>客户列表</h2><label class="pdm-inline-search"><Search :size="15" /><input v-model="query" placeholder="搜索客户编码或名称"></label></header>
      <div class="pdm-table-scroll"><table class="pdm-project-table"><thead><tr><th>客户编码</th><th>客户名称</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="customer in filteredCustomers" :key="customer.id"><td><strong>{{ customer.code }}</strong></td><td>{{ customer.name }}</td><td><span :class="customer.isActive ? 'pdm-status is-ok' : 'pdm-status is-warn'">{{ customer.isActive ? '启用' : '停用' }}</span></td><td><button type="button" class="pdm-text-action" @click="openEdit(customer)">编辑</button></td></tr></tbody></table></div>
    </section>
    <el-dialog v-model="dialogOpen" :title="draft.id ? '编辑客户' : '新增客户'" width="520px" :close-on-click-modal="false">
      <form class="pdm-project-form" @submit.prevent="submit"><label>客户编码<input v-model="draft.code" maxlength="30" placeholder="例如 C00465"></label><label>客户名称<input v-model="draft.name" maxlength="200"></label><label class="pdm-checkbox-field"><input v-model="draft.isActive" type="checkbox">启用客户</label></form>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="dialogOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="submit">保存</button></template>
    </el-dialog>
  </section>
</template>
