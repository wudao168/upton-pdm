<script setup lang="ts">
import { computed } from 'vue'
import type { MaterialRelationTemplate } from '../types'
import { u9UnitName } from '../u9Units'
import { relationMains } from '../materialRelationMains'
import AlignedDetailTable from './AlignedDetailTable.vue'
import type { MaterialRelationOption } from '../types'
const props = defineProps<{ relation: MaterialRelationTemplate; showDraft?: boolean }>()
const revision = computed(() => (props.showDraft ? props.relation.draftRevision : null) ?? props.relation.publishedRevision)
const perMain = (mode: unknown) => mode === 'PerMainQuantity' || mode === 0
const mainColumns = [
  { key: 'code', label: '物料编码', parentIndex: 1, width: 150 }, { key: 'name', label: '物料名称', parentIndex: 2, width: 180 },
  { key: 'model', label: '规格', parentIndex: 3, width: 190 }, { key: 'brand', label: '品牌', parentIndex: 4, width: 100 },
  { key: 'category', label: '标准物料分类', parentIndex: 5, width: 150 },
]
const optionColumns = [
  ...mainColumns.slice(0, 4), { key: 'quantity', label: '每套数量', parentIndex: 6, width: 85 },
  { key: 'unit', label: '单位', parentIndex: 7, width: 100 }, { key: 'calculation', label: '数量计算', parentIndex: 8, width: 105 },
  { key: 'default', label: '默认项', parentIndex: 9, width: 75 }, { key: 'advice', label: '选型建议', parentIndex: 10, width: 140 },
]
const mainRows = computed(() => relationMains(props.relation, props.showDraft).map(main => ({ code: main.materialCode, name: main.materialName, model: main.specification ?? undefined, brand: main.brand ?? undefined, category: main.standardLibraryCategories?.map(category => category.name).join('、') || '未分类（待补选）' })))
function optionRows(options: MaterialRelationOption[]) {
  return options.map(option => ({ code: option.materialCode, name: option.materialName, model: option.specification ?? undefined, brand: option.brand ?? undefined, quantity: option.quantityPerSet, unit: u9UnitName(option.unitCode), calculation: perMain(option.quantityMode) ? '随主料数量' : '固定数量', default: option.isDefault ? '是' : '否', advice: option.selectionAdvice ?? undefined }))
}
</script>

<template>
  <section class="material-relation-details" :aria-label="`关联配置 ${relationMains(relation, showDraft)[0]?.materialCode || relation.mainMaterialCode} 的配件明细`">
    <AlignedDetailTable :columns="mainColumns" :rows="mainRows" label="关联配置适用主物料" />
    <section v-for="group in revision?.groups ?? []" :key="group.id" class="material-relation-details__group">
      <AlignedDetailTable :columns="optionColumns" :rows="optionRows(group.options)" :label="`${group.name} 的候选物料`" />
    </section>
    <p v-if="!revision">该配置尚未发布生效，暂无可选用的配件明细。</p>
  </section>
</template>

<style scoped>
.material-relation-details{min-width:0;padding:0;background:var(--pdm-surface-muted,var(--pdm-surface));font-size:12px}.material-relation-details p{color:var(--pdm-muted)}.material-relation-details__group{margin-top:0}
</style>
