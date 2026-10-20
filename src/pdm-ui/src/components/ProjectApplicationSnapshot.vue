<script setup lang="ts">
import type { ProjectApplicationData } from '../projectApplications'
defineProps<{ data: ProjectApplicationData }>()
</script>

<template>
  <dl class="application-snapshot">
    <dt>客户名称</dt><dd>{{ data.input.customerName || '—' }}</dd>
    <dt>项目名称</dt><dd>{{ data.input.projectName || '—' }}</dd>
    <dt>立项方式</dt><dd>{{ data.input.isSpecial ? '特殊立项' : '正常立项' }}</dd>
    <dt>订单日期</dt><dd>{{ data.input.orderDate || '未填写' }}</dd>
    <dt>交货日期</dt><dd>{{ data.input.deliveryDate || '—' }}</dd>
    <dt>销售人员</dt><dd>{{ data.salesName || '—' }}</dd>
    <dt>售前工程师</dt><dd>{{ data.presalesPeople.map(x => x.displayName).join('、') || '—' }}</dd>
    <dt>备注</dt><dd>{{ data.input.remarks || '—' }}</dd>
  </dl>
</template>

<style scoped>
.application-snapshot{display:grid;grid-template-columns:95px 1fr 95px 1fr;border-top:1px solid var(--pdm-border-soft);border-left:1px solid var(--pdm-border-soft);font-size:12px}
dt,dd{margin:0;padding:9px;border-right:1px solid var(--pdm-border-soft);border-bottom:1px solid var(--pdm-border-soft);white-space:pre-wrap;overflow-wrap:anywhere}
dt{background:var(--pdm-table-header);color:var(--pdm-muted)}
@media(max-width:700px){.application-snapshot{grid-template-columns:90px 1fr}}
</style>
