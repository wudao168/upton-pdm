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
.nameplate-preview-size{width:560px;max-width:100%;margin-inline:auto}.nameplate-preview{zoom:calc(2 / 3);user-select:text;border:2px solid #333;border-radius:22px;padding:18px;background:#fff;color:#222;overflow-x:auto}.nameplate-value{display:block;text-align:center}.nameplate-brand{position:relative;display:flex;align-items:center;min-height:128px;margin-bottom:14px;min-width:620px}.nameplate-brand>div{position:absolute;left:50%;transform:translateX(-50%);text-align:center;white-space:nowrap}.nameplate-brand img{width:160px;height:102.4px;flex:0 0 160px;object-fit:contain;filter:brightness(0)}.nameplate-brand h2{margin:0;font-size:22px;letter-spacing:1px}.nameplate-brand p{margin:4px 0 0;font-size:16px;letter-spacing:1px}.nameplate-table{width:100%;min-width:620px;border-collapse:collapse;table-layout:fixed;border:2px solid #333;font-size:16px}.nameplate-label{width:18%}.nameplate-table th,.nameplate-table td{border:2px solid #333;padding:8px;font-weight:500;overflow-wrap:anywhere}.nameplate-table th{text-align:center;font-weight:600;font-size:17px}.nameplate-table td[colspan="3"]{text-align:center}.nameplate-table th span{display:block;font-size:12px;letter-spacing:.5px;margin-top:3px}.nameplate-table :deep(.el-input__wrapper){padding:1px 5px}.nameplate-table :deep(.el-date-editor){width:100%;min-width:0}.nameplate-power{text-align:center;font-size:22px}.nameplate-preview footer{text-align:right;min-width:620px;margin-top:14px;font-size:18px;letter-spacing:1px}
</style>
