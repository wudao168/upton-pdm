<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref } from 'vue'
import { useReportChartMotion } from '../reportChartMotion'
import { chartMaximum, chartNumber, chartSegments, cumulativePercent, type ReportChartDatum, type ReportChartSeries } from '../reportCharts'
const props=withDefaults(defineProps<{kind:'columns'|'line'|'area'|'pareto'|'donut'|'gauge'|'scatter';label:string;data?:ReportChartDatum[];series?:ReportChartSeries[];percentage?:boolean;value?:number|null;center?:string;caption?:string;color?:string;height?:number;cumulativeTotal?:number;active?:boolean}>(),{data:()=>[],series:()=>[],height:162,color:'#3988ff'})
const emit=defineEmits<{select:[key:string]}>()
const host=ref<HTMLElement>(),width=ref(320),tip=ref(''),tipX=ref(0),tipY=ref(0)
let resize:ResizeObserver|undefined
onMounted(()=>{resize=new ResizeObserver(entries=>{width.value=Math.max(160,entries[0]?.contentRect.width??320)});if(host.value)resize.observe(host.value)})
onBeforeUnmount(()=>resize?.disconnect())
const left=34,top=18
const bottom=computed(()=>props.height-32),right=computed(()=>width.value-(props.kind==='pareto'?31:12)),plotWidth=computed(()=>right.value-left)
const maximum=computed(()=>chartMaximum([...props.data.map(d=>d.value),...props.series.flatMap(s=>s.values)],props.percentage))
const minimum=computed(()=>Math.min(0,...props.data.map(d=>d.value??0),...props.series.flatMap(s=>s.values.map(v=>v??0))))
const y=(v:number)=>bottom.value-(v-minimum.value)/(maximum.value-minimum.value)*(bottom.value-top)
const slots=computed(()=>props.kind==='scatter'?[...new Set(props.data.map(d=>d.group||d.label))]:props.data.map(d=>d.label))
const x=(i:number)=>left+(i+.5)*plotWidth.value/Math.max(1,slots.value.length)
const barWidth=computed(()=>Math.min(42,plotWidth.value/Math.max(1,props.data.length)*.55))
const total=computed(()=>props.data.reduce((s,d)=>s+Math.max(0,d.value??0),0))
const cumulative=computed(()=>cumulativePercent(props.data.map(d=>d.value),props.cumulativeTotal))
const seriesList=computed(()=>props.series.length?props.series:[{label:props.label,color:props.color,values:props.data.map(d=>d.value)}])
const lines=(values:(number|null)[])=>chartSegments(values,x,y)
const empty=computed(()=>props.kind==='gauge'?props.value==null:!props.data.some(d=>d.value!=null)&&!props.series.some(s=>s.values.some(v=>v!=null)))
const arcX=computed(()=>Math.min(85,width.value*.27)),arcY=computed(()=>props.height/2-3),radius=computed(()=>Math.min(52,props.height*.32,width.value*.20))
const circumference=computed(()=>2*Math.PI*radius.value)
const offset=(index:number)=>-props.data.slice(0,index).reduce((s,d)=>s+Math.max(0,d.value??0),0)/Math.max(1,total.value)*circumference.value
const gaugeX=computed(()=>width.value/2),gaugeRadius=computed(()=>Math.min(width.value/2-18,64))
const gaugePath=computed(()=>'M'+(gaugeX.value-gaugeRadius.value)+',95 A'+gaugeRadius.value+','+gaugeRadius.value+' 0 0 1 '+(gaugeX.value+gaugeRadius.value)+',95')
const textLines=(s:string)=>{const n=Math.max(2,Math.min(6,Math.floor((plotWidth.value/Math.max(1,slots.value.length)-8)/10)));return s.length>n?[s.slice(0,n),s.slice(n,n*2-1)+(s.length>n*2-1?'…':'')]:[s]}
const description=(d:ReportChartDatum)=>d.hint||d.label+'：'+(d.value==null?'暂无数据':chartNumber(d.value)+(props.percentage?'%':''))
function hover(event:MouseEvent|FocusEvent,message:string){tip.value=message;const rect=host.value?.getBoundingClientRect();const target=(event.currentTarget as Element).getBoundingClientRect();tipX.value=Math.max(8,Math.min(width.value-190,target.left-(rect?.left??0)));tipY.value=Math.min(props.height-24,Math.max(0,target.top-(rect?.top??0)-30))}
function choose(d:ReportChartDatum){tip.value='';emit('select',d.key)}
useReportChartMotion(host,()=>props.active!==false,()=>y(0),()=>[props.kind,props.data,props.series,props.value])
</script>
<template>
<div ref="host" class="report-plot" :data-chart="kind" @mouseleave="tip=''">
 <svg :viewBox="'0 0 '+width+' '+height" :style="{height:height+'px'}" role="group" :aria-label="label">
  <template v-if="kind==='donut'">
   <circle :cx="arcX" :cy="arcY" :r="radius" class="chart-ring-track" fill="none" stroke-width="17" />
   <circle v-for="(d,i) in data" :key="d.key" :cx="arcX" :cy="arcY" :r="radius" fill="none" :stroke="d.color||color" stroke-width="17" data-chart-motion="arc" :stroke-dasharray="Math.max(0,d.value??0)/Math.max(1,total)*circumference+' '+circumference" :stroke-dashoffset="offset(i)" :transform="'rotate(-90 '+arcX+' '+arcY+')'" class="chart-hit" tabindex="0" role="button" :aria-label="description(d)" @click="choose(d)" @keydown.enter.prevent="choose(d)" @keydown.space.prevent="choose(d)" @mouseenter="hover($event,description(d))" @focus="hover($event,description(d))" @blur="tip=''"><title>{{description(d)}}</title></circle>
   <text :x="arcX" :y="arcY+2" class="chart-big" text-anchor="middle">{{center??total}}</text><text :x="arcX" :y="arcY+19" text-anchor="middle">{{caption||'项次'}}</text>
   <g v-for="(d,i) in data" :key="d.key" class="chart-hit" tabindex="0" role="button" :aria-label="description(d)" @click="choose(d)" @keydown.enter.prevent="choose(d)" @mouseenter="hover($event,description(d))" @focus="hover($event,description(d))" @blur="tip=''">
    <rect :x="arcX+radius+14" :y="17+i*24" :width="Math.max(1,width-arcX-radius-14)" height="21" fill="transparent" stroke="none"/><rect :x="arcX+radius+18" :y="24+i*24" width="7" height="7" rx="2" :fill="d.color||color"/><text :x="arcX+radius+31" :y="31+i*24">{{d.label}}</text><text :x="width-5" :y="31+i*24" text-anchor="end" class="chart-value">{{d.value??'—'}}</text>
   </g>
  </template>
  <template v-else-if="kind==='gauge'">
   <path :d="gaugePath" fill="none" class="chart-ring-track" stroke-width="14" stroke-linecap="round"/>
   <path v-if="value!=null" :d="gaugePath" fill="none" :stroke="color" stroke-width="14" stroke-linecap="round" pathLength="100" data-chart-motion="arc" :stroke-dasharray="Math.min(100,Math.max(0,value))+' 100'"/>
   <text :x="gaugeX" y="84" class="chart-big" text-anchor="middle">{{value==null?'—':value.toFixed(1)+'%'}}</text><text :x="gaugeX" y="109" text-anchor="middle">{{caption||'预算执行率'}}</text>
   <text x="12" y="130">0%</text><text :x="width-12" y="130" text-anchor="end">100%</text>
  </template>
  <template v-else>
   <g v-for="n in [0,1,2]" :key="n"><line :x1="left" :x2="right" :y1="y(minimum+(maximum-minimum)*n/2)" :y2="y(minimum+(maximum-minimum)*n/2)" class="chart-grid"/><text :x="left-7" :y="y(minimum+(maximum-minimum)*n/2)+3" text-anchor="end">{{chartNumber(minimum+(maximum-minimum)*n/2)}}{{percentage?'%':''}}</text></g>
   <line :x1="left" :x2="right" :y1="y(0)" :y2="y(0)" class="chart-axis"/>
   <g v-for="(label,i) in slots" :key="label"><text v-for="(line,j) in textLines(label)" :key="j" :x="x(i)" :y="height-17+j*11" text-anchor="middle">{{line}}</text></g>
   <template v-if="kind==='columns'||kind==='pareto'">
    <g v-for="(d,i) in data" :key="d.key" class="chart-hit" tabindex="0" role="button" :aria-label="description(d)" @click="choose(d)" @keydown.enter.prevent="choose(d)" @keydown.space.prevent="choose(d)" @mouseenter="hover($event,description(d))" @focus="hover($event,description(d))" @blur="tip=''">
     <rect :x="x(i)-barWidth/2-5" :y="top-8" :width="barWidth+10" :height="bottom-top+8" fill="transparent"/>
     <rect data-chart-motion="bar" v-if="d.value!=null" :x="x(i)-barWidth/2" :y="d.value>=0?y(d.value):y(0)" :width="barWidth" :height="Math.max(1,Math.abs(y(d.value)-y(0)))" rx="3" :fill="d.color||color"/>
     <text :x="x(i)" :y="(d.value!=null?Math.min(y(d.value),y(0)):bottom)-5" text-anchor="middle" class="chart-value" data-chart-motion="label">{{d.value==null?'—':chartNumber(d.value)}}</text><title>{{description(d)}}</title>
    </g>
    <template v-if="kind==='pareto'&&total>0"><path v-for="(path,i) in chartSegments(cumulative,x,v=>bottom-v/100*(bottom-top))" :key="i" :d="path" data-chart-motion="line" stroke="#98ca23" stroke-width="2" fill="none"/><circle data-chart-motion="point" v-for="(v,i) in cumulative" :key="i" :cx="x(i)" :cy="bottom-(v??0)/100*(bottom-top)" r="3" fill="#fff" stroke="#98ca23"/><text :x="right+4" :y="top+3">100%</text><text :x="right+4" :y="bottom+3">0%</text></template>
   </template>
   <template v-else-if="kind==='line'||kind==='area'">
    <g v-for="(s,seriesIndex) in seriesList" :key="s.label">
     <template v-if="kind==='area'"><path data-chart-motion="area" v-for="(path,i) in lines(s.values)" :key="i" :d="path+' L'+path.split(/[ML]/).filter(Boolean).at(-1)!.trim().split(',')[0]+','+y(0)+' L'+path.slice(1).split(',')[0]+','+y(0)+' Z'" :fill="s.color" fill-opacity=".12"/></template>
     <path v-for="(path,i) in lines(s.values)" :key="i" :d="path" data-chart-motion="line" :stroke="s.color" stroke-width="2" fill="none"/>
     <template v-for="(v,i) in s.values" :key="i"><circle data-chart-motion="point" v-if="v!=null" :cx="x(i)" :cy="y(v)" :r="seriesIndex?5:3.5" :fill="seriesIndex?'transparent':s.color" :stroke="seriesIndex?s.color:'var(--pdm-bg,#fff)'" :stroke-width="seriesIndex?2:1.5" class="chart-hit" tabindex="0" role="button" :aria-label="seriesList.map(item=>item.hints?.[i]||item.label+' '+data[i]?.label+'：'+(item.values[i]??'暂无数据')+(percentage?'%':'')).join('；')" @click="emit('select',data[i]?.key||'')" @keydown.enter.prevent="emit('select',data[i]?.key||'')" @mouseenter="hover($event,seriesList.map(item=>item.hints?.[i]||item.label+' '+data[i]?.label+'：'+(item.values[i]??'暂无数据')+(percentage?'%':'')).join('；'))" @focus="hover($event,seriesList.map(item=>item.hints?.[i]||item.label+' '+data[i]?.label+'：'+(item.values[i]??'暂无数据')+(percentage?'%':'')).join('；'))" @blur="tip=''"/><text v-else :x="x(i)" :y="bottom-5" text-anchor="middle" :fill="s.color">—</text></template>
    </g>
   </template>
   <template v-else-if="kind==='scatter'">
    <circle data-chart-motion="point" v-for="(d,i) in data.filter(d=>d.value!=null)" :key="d.key" :cx="x(slots.indexOf(d.group||d.label))+((i%5)-2)*5" :cy="y(d.value!)" r="4" :fill="d.color||color" fill-opacity=".65" stroke="var(--pdm-bg,#fff)" class="chart-hit" tabindex="0" role="button" :aria-label="description(d)" @click="choose(d)" @keydown.enter.prevent="choose(d)" @mouseenter="hover($event,description(d))" @focus="hover($event,description(d))" @blur="tip=''"><title>{{description(d)}}</title></circle>
   </template>
   <text v-if="empty" :x="(left+right)/2" :y="(top+bottom)/2" text-anchor="middle" class="chart-empty">暂无数据</text>
  </template>
 </svg>
 <div v-if="tip" class="report-chart-tooltip" role="tooltip" :style="{left:tipX+'px',top:tipY+'px'}">{{tip}}</div>
</div>
</template>
<style scoped>
.report-plot{position:relative;width:100%;min-width:0}.report-plot svg{display:block;width:100%;overflow:visible}.report-plot text{font-family:var(--pdm-font-ui);font-size:10px;fill:var(--report-muted,#76808e)}.report-plot .chart-value{fill:var(--pdm-text,#263445);font-weight:600}.report-plot .chart-big{fill:var(--pdm-text,#263445);font-size:23px;font-weight:650;font-variant-numeric:tabular-nums}.chart-grid{stroke:var(--pdm-border-soft,#e9edf2);stroke-dasharray:3 4}.chart-axis{stroke:var(--pdm-border,#dbe1e8)}.chart-ring-track{stroke:var(--report-surface,#f1f4f7)}.chart-hit{cursor:pointer;outline:none}.chart-hit:hover,.chart-hit:focus{filter:brightness(.90);stroke:var(--pdm-theme-accent,#0ab88a);stroke-width:1}.report-chart-tooltip{pointer-events:none;position:absolute;z-index:5;max-width:230px;padding:6px 9px;border-radius:5px;background:var(--pdm-text,#263445);color:var(--pdm-bg,#fff);font-size:11px;line-height:1.5;box-shadow:0 3px 12px #0002}.chart-empty{font-size:12px!important}
</style>
