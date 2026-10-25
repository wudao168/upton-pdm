<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { EngineeringKit } from '../types'
import { u9UnitName } from '../u9Units'

const props = defineProps<{ kit: EngineeringKit | null; canManage?: boolean }>()
const emit = defineEmits<{ close: [] }>()
const revisionId = ref('')
const revisions = computed(() => props.kit?.revisions.filter(item => props.canManage || item.state === 'Released').slice().sort((a, b) => b.versionNumber - a.versionNumber) ?? [])
const revision = computed(() => revisions.value.find(item => item.id === revisionId.value))
watch(() => props.kit, kit => {
  revisionId.value = (props.canManage ? revisions.value.find(item => item.state === 'Draft')?.id : undefined)
    ?? kit?.currentReleasedRevisionId ?? revisions.value[0]?.id ?? ''
}, { immediate: true })
</script>

<template>
  <el-dialog :model-value="!!kit" title="套件明细" width="min(1100px, calc(100vw - 32px))" @close="emit('close')">
    <template v-if="kit">
      <div class="kit-detail-heading"><strong>{{ kit.code || '套件草稿' }} · {{ kit.name }}</strong><el-tag v-if="kit.isActive === false" type="info">已停用</el-tag><span>型号：{{ kit.model || '发布时生成' }}</span><span>品牌：{{ kit.brand }}</span>
        <el-select v-model="revisionId" aria-label="套件版本"><el-option v-for="item in revisions" :key="item.id" :value="item.id" :label="`V${String(item.versionNumber).padStart(2, '0')} ${item.state === 'Draft' ? '草稿' : '已发布'}`" /></el-select>
      </div>
      <el-table :data="revision?.components ?? []" max-height="480" empty-text="该版本暂无明细">
        <el-table-column prop="materialCode" label="料号" min-width="135" ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.materialCode }}</span></template></el-table-column>
        <el-table-column prop="materialName" label="名称" min-width="150" ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.materialName }}</span></template></el-table-column>
        <el-table-column prop="specification" label="型号" min-width="160" />
        <el-table-column prop="brand" label="品牌" min-width="95" />
        <el-table-column label="单位" width="70"><template #default="{ row }">{{ u9UnitName(row.unit) }}</template></el-table-column>
        <el-table-column prop="quantity" label="每套数量" width="100" />
        <el-table-column prop="remark" label="备注" min-width="140" />
      </el-table>
    </template>
    <template #footer><el-button @click="emit('close')">关闭</el-button></template>
  </el-dialog>
</template>

<style scoped>
.kit-detail-heading{display:flex;align-items:center;flex-wrap:wrap;gap:16px;margin-bottom:14px}.kit-detail-heading .el-select{width:160px;margin-left:auto}
</style>
