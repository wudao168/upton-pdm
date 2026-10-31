<script setup lang="ts">
import type { NameplateContent } from '../projectNameplate'
import { useUserDisplayName } from '../userDisplay'
import NameplatePreview from './NameplatePreview.vue'
import NameplateQr from './NameplateQr.vue'
defineProps<{ content: NameplateContent }>()
const displayName = useUserDisplayName()
</script>

<template>
  <section class="nameplate-content" aria-label="定版铭牌内容">
    <header class="nameplate-content__header"><p>按公司统一铭牌标准制作，以下为硬件已确认的生产内容。扫码设备序列号可定位铭牌生产任务。</p><NameplateQr :serial-number="content.serialNumber" /></header>
    <NameplatePreview :name="content.name" :model="content.model" :serial-numbers="[content.serialNumber]" :power-supply="content.powerSupply" :rated-power="`${content.ratedPower} kW`" :dimensions="content.dimensions" :air-pressure="content.airPressure" :weight="content.weight" :factory-date="content.factoryDate" />
    <dl>
      <dt>设备名称</dt><dd>{{ content.name }}</dd>
      <dt>型号</dt><dd>{{ content.model }}</dd>
      <dt>序列号</dt><dd>{{ content.serialNumber }}</dd>
      <dt>电源</dt><dd>{{ content.powerSupply }}</dd>
      <dt>额定功率</dt><dd>{{ content.ratedPower }} kW</dd>
      <dt>气源压力</dt><dd>{{ content.airPressure }}</dd>
      <dt>设备外形尺寸</dt><dd>{{ content.dimensions }}</dd>
      <dt>设备重量</dt><dd>{{ content.weight }}</dd>
      <dt>出厂月份</dt><dd>{{ content.factoryDate.slice(0, 7) }}</dd>
      <dt>硬件确认人</dt><dd>{{ displayName(content.confirmedBy) }}</dd>
      <dt>确认时间</dt><dd>{{ new Date(content.confirmedAt).toLocaleString('zh-CN', { hour12: false }) }}</dd>
    </dl>
  </section>
</template>

<style scoped>
.nameplate-content{flex:1;min-width:0;min-height:0;overflow-y:auto;user-select:text}.nameplate-content__header{display:flex;align-items:flex-start;justify-content:space-between;gap:12px;margin-bottom:12px}.nameplate-content__header p{flex:1}.nameplate-content p{color:var(--pdm-muted);line-height:1.6}.nameplate-content dl{display:grid;grid-template-columns:minmax(96px, 1fr) minmax(0, 3fr);margin:12px 0;border:1px solid var(--pdm-border)}.nameplate-content dt,.nameplate-content dd{margin:0;padding:8px 10px;border-bottom:1px solid var(--pdm-border);overflow-wrap:anywhere}.nameplate-content dt{font-weight:600;background:var(--pdm-surface-muted)}.nameplate-content dt:last-of-type,.nameplate-content dd:last-of-type{border-bottom:0}
</style>
