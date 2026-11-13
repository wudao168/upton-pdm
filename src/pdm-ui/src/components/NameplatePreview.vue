<script setup lang="ts">
import logo from '../assets/upton-logo-white.png'

defineProps<{
  name: string
  model: string
  serialNumbers: string[]
  powerSupply: string
  ratedPower: string
  dimensions: string
  airPressure: string
  weight: string
  factoryDate: string | null
}>()
</script>

<template>
  <div class="nameplate-preview-size" aria-label="铭牌效果图"><div class="nameplate-preview" aria-label="铭牌信息">
    <header class="nameplate-brand"><img :src="logo" alt="UPTON"><div><h2>昆山阿普顿自动化系统有限公司</h2><p>UPTON AUTOMATION SYSTEMS Co., Ltd.</p></div></header>
    <table class="nameplate-table"><colgroup><col class="nameplate-label"><col><col class="nameplate-label"><col></colgroup><tbody>
      <tr><th>电源<span>Power Supply</span></th><td><slot name="powerSupply"><span class="nameplate-value">{{ powerSupply || '—' }}</span></slot></td><th>额定功率<span>Rated Power</span></th><td class="nameplate-power" aria-label="额定功率">{{ ratedPower }}</td></tr>
      <tr><th>气源压力<span>Air Pressure</span></th><td><slot name="airPressure"><span class="nameplate-value">{{ airPressure || '—' }}</span></slot></td><th>设备外形尺寸<span>Overall Dimensions</span></th><td><slot name="dimensions"><span class="nameplate-value">{{ dimensions || '—' }}</span></slot></td></tr>
      <tr><th>重量<span>Weight</span></th><td><slot name="weight"><span class="nameplate-value">{{ weight || '—' }}</span></slot></td><th>出厂日期<span>Mfg. Date</span></th><td><slot name="factoryDate"><span class="nameplate-value">{{ factoryDate?.slice(0, 7) || '—' }}</span></slot></td></tr>
      <tr><th>型号<span>Model</span></th><td colspan="3">{{ model || '—' }}</td></tr>
      <tr><th>序列号<span>S/N</span></th><td colspan="3">{{ serialNumbers.join('、') || '—' }}</td></tr>
      <tr><th>设备名称<span>Equipment Name</span></th><td colspan="3">{{ name || '—' }}</td></tr>
    </tbody></table>
    <footer>MADE IN CHINA</footer>
  </div></div>
</template>

<style scoped>
.nameplate-preview-size{width:560px;max-width:100%;margin-inline:auto;container-type:inline-size}
.nameplate-preview{user-select:text;border:1px solid #333;border-radius:16px;padding:12px;background:#fff;color:#222}
.nameplate-brand{display:flex;align-items:center;gap:12px;min-height:76px;margin-bottom:10px}
.nameplate-brand>div{flex:1;min-width:0;text-align:center}
.nameplate-brand img{width:20%;max-width:108px;height:auto;object-fit:contain;filter:brightness(0)}
.nameplate-brand h2{margin:0;font-size:16px;line-height:1.4}
.nameplate-brand p{margin:4px 0 0;font-size:11px}
.nameplate-table{width:100%;border-collapse:collapse;table-layout:fixed;border:1px solid #333;font-size:16px;line-height:1.4}
.nameplate-label{width:16%}
.nameplate-table th,.nameplate-table td{border:1px solid #333;padding:5px;font-weight:600;overflow-wrap:anywhere}
.nameplate-table td{text-align:center}
.nameplate-table th{font-size:12px}
.nameplate-table th span{display:block;font-size:10px;line-height:1.2;margin-top:3px}
.nameplate-table :deep(.el-input__wrapper){padding:1px 6px}
.nameplate-table :deep(.el-input__inner){font-size:16px;font-weight:600;color:#222}
.nameplate-table :deep(input:disabled){-webkit-text-fill-color:#555}
.nameplate-table :deep(.el-date-editor){width:100%;min-width:0}
.nameplate-power{font-size:18px}
.nameplate-preview footer{text-align:right;margin-top:10px;font-size:12px;letter-spacing:.6px}
@container (max-width:400px){
  .nameplate-preview{padding:7px}
  .nameplate-table th,.nameplate-table td{padding:3px}
  .nameplate-label{width:19%}
}
</style>
