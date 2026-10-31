<script setup lang="ts">
import { computed } from 'vue'
import type { MaterialRelationTemplate } from '../types'
import { u9UnitName } from '../u9Units'
const props = defineProps<{ relation: MaterialRelationTemplate; showDraft?: boolean }>()
const revision = computed(() => (props.showDraft ? props.relation.draftRevision : null) ?? props.relation.publishedRevision)
const single = (mode: unknown) => mode === 'Single' || mode === 0
const perMain = (mode: unknown) => mode === 'PerMainQuantity' || mode === 0
</script>

<template>
  <section class="material-relation-details" :aria-label="`关联配置 ${relation.mainMaterialCode} 的配件明细`">
    <header><strong>{{ relation.name }}</strong><span v-if="revision">V{{ revision.version }} · {{ showDraft && relation.draftRevision ? '草稿' : '已生效' }}</span></header>
    <section v-for="group in revision?.groups ?? []" :key="group.id" class="material-relation-details__group">
      <header><strong>{{ group.name }}</strong><span>{{ group.isRequired ? '必选' : '可选' }} · {{ single(group.selectionMode) ? '单选' : '多选' }} · 最少 {{ group.minSelection }} 项<span v-if="group.maxSelection != null">，最多 {{ group.maxSelection }} 项</span></span></header>
      <el-table :data="group.options" max-height="300" stripe>
        <el-table-column prop="materialCode" label="料号" min-width="135"><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.materialCode }}</span></template></el-table-column>
        <el-table-column prop="materialName" label="名称" min-width="150" /><el-table-column prop="specification" label="型号" min-width="145" /><el-table-column prop="brand" label="品牌" min-width="90" />
        <el-table-column label="单位" width="70"><template #default="{ row }">{{ u9UnitName(row.unitCode) }}</template></el-table-column>
        <el-table-column label="数量计算" min-width="105"><template #default="{ row }">{{ perMain(row.quantityMode) ? '随主料数量' : '固定数量' }}</template></el-table-column>
        <el-table-column prop="quantityPerSet" label="每套数量" width="90" /><el-table-column label="默认项" width="75"><template #default="{ row }">{{ row.isDefault ? '是' : '否' }}</template></el-table-column>
        <el-table-column prop="selectionAdvice" label="选型建议" min-width="140" />
      </el-table>
    </section>
    <p v-if="!revision">该配置尚未发布生效，暂无可选用的配件明细。</p>
  </section>
</template>

<style scoped>
.material-relation-details{min-width:0;padding:10px;background:var(--pdm-surface-muted,var(--pdm-surface));font-size:12px}.material-relation-details header{display:flex;align-items:center;flex-wrap:wrap;gap:12px;margin-bottom:8px}.material-relation-details header>span,.material-relation-details p{color:var(--pdm-muted)}.material-relation-details__group{margin-top:12px}
</style>
