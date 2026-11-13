<script setup lang="ts">
import { Building2, CalendarCheck, FilePenLine, FolderKanban, PencilRuler, TriangleAlert, Wallet } from '@lucide/vue'
import DepartmentSelectField from './DepartmentSelectField.vue'
import DepartmentName from './DepartmentName.vue'
import CompanyName from './CompanyName.vue'
import CompanyNameField from './CompanyNameField.vue'
import PersonName from './PersonName.vue'
import { computed, ref, watch, onBeforeUnmount } from 'vue'
import ProjectReportChart from './ProjectReportChart.vue'
import { type ReportChartDatum } from '../reportCharts'
import { readProjectReporting } from '../api'
import { useUserDisplayName } from '../userDisplay'
const displayUserName = useUserDisplayName()
import { addDays, type WorkbenchTab } from '../projectWorkbench'
import { buildReportingRows, drawingStats, rate, knownSum, money, nextDue, expenseNames, targetTab, type ReportingData, type ReportRow } from '../projectReporting'
const props=defineProps<{companyName?:string;active?:boolean;token?:string;companyId?:string;currentUsername?:string}>()
const emit=defineEmits<{open:[projectId:string,tab:WorkbenchTab]}>()
const today=()=>new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date())
const day=ref(today()),from=ref(day.value.slice(0,4)+'-01-01'),to=ref(day.value),unit=ref(''),customer=ref(''),manager=ref(''),stage=ref(''),search=ref('')
const data=ref<ReportingData|null>(null),loading=ref(false),error=ref(''),tab=ref('阶段分析'),metric=ref('项目数'),drill=ref(''),page=ref(1),sort=ref('code'),days=ref(30),dueKind=ref('交货')
const detailDialog=ref<HTMLDialogElement>(),methodDialog=ref<HTMLDialogElement>()
let run=0,abort:AbortController|undefined
async function refresh(){const current=++run;abort?.abort();abort=new AbortController();error.value='';loading.value=true;day.value=today();try{if(!props.token){data.value=null;error.value='请登录后查看授权范围内的项目报表。';return}const response=await readProjectReporting(props.token,abort.signal);if(current===run)data.value=response}catch(e){if(current===run){data.value=null;error.value='项目报表读取失败，请检查网络或登录状态后刷新重试。';console.error('项目报表读取失败',e)}}finally{if(current===run)loading.value=false}}
watch(()=>[props.token,props.companyId,props.currentUsername],()=>{data.value=null;unit.value='';customer.value='';manager.value='';stage.value='';drill.value='';if(props.active!==false)void refresh()},{immediate:true})
watch(()=>props.active,value=>{if(value&&!data.value&&!loading.value)void refresh()})
onBeforeUnmount(()=>{run++;abort?.abort()})
const invalidDates=computed(()=>!from.value||!to.value||from.value>to.value||to.value>day.value)
const all=computed(()=>data.value&&!invalidDates.value?buildReportingRows(data.value,day.value,from.value,to.value):[])
const options=(key:'unit'|'customer'|'manager'|'stage')=>computed(()=>[...new Set(all.value.map(r=>r[key]).filter(Boolean))].sort())
const units=options('unit'),customers=options('customer'),managers=options('manager'),stages=options('stage')
const rows=computed(()=>all.value.filter(r=>(!unit.value||r.unit===unit.value)&&(!customer.value||r.customer===customer.value)&&(!manager.value||r.manager===manager.value)&&(!stage.value||r.stage===stage.value)&&(!search.value||`${r.code} ${r.name}`.toLowerCase().includes(search.value.toLowerCase()))))
const settlement=computed(()=>knownSum(rows.value.map(r=>r.finance?.settlement))),customerCount=computed(()=>new Set(rows.value.map(r=>r.customerKey).filter(Boolean)).size)
const settlementCount=computed(()=>rows.value.filter(r=>r.finance?.settlement!=null).length)
const delivery=computed(()=>rate(rows.value,'deliveryResult')),design=computed(()=>rate(rows.value,'designResult')),drawings=computed(()=>drawingStats(rows.value,from.value,to.value))
const expenseSample=computed(()=>rows.value.filter(r=>r.finance?.budget!=null&&r.finance.actual!=null))
const overruns=computed(()=>expenseSample.value.filter(r=>r.finance!.actual!>r.finance!.budget!))
const budget=computed(()=>knownSum(expenseSample.value.map(r=>r.finance!.budget))),actual=computed(()=>knownSum(expenseSample.value.map(r=>r.finance!.actual)))
const percent=(n:number|null)=>n==null?'—':`${n.toFixed(1)}%`
const colors=['#3485ff','#99d900','#06c5bd','#af74ef','#f15ab5','#f4b942','#6875db']
const distribution=computed(()=>[...new Set(rows.value.map(r=>r.stage))].map((label,i)=>{const group=rows.value.filter(r=>r.stage===label);return {label,count:group.length,customers:new Set(group.map(r=>r.customerKey).filter(Boolean)).size,amount:knownSum(group.map(r=>r.finance?.settlement)),color:colors[i%colors.length]}}))
const distValue=(r:typeof distribution.value[number])=>metric.value==='金额'?r.amount??0:metric.value==='客户数'?r.customers:r.count
const dueOf=(r:ReportRow)=>dueKind.value==='设计'?r.designResult.due:r.deliveryDate
const upcoming=computed(()=>dueKind.value==='交货'?nextDue(rows.value,day.value,days.value):rows.value.filter(r=>!['completed','cancelled','terminated'].includes(r.state)&&!r.designResult.actual&&r.designResult.due&&r.designResult.due>=day.value&&r.designResult.due<=addDays(day.value,days.value)))
const calendar=computed(()=>Array.from({length:days.value+1},(_,i)=>{const date=addDays(day.value,i);return {date,count:upcoming.value.filter(r=>dueOf(r)===date).length}}))

const risks=computed(()=>rows.value.filter(r=>r.riskLabels.length).sort((a,b)=>b.riskLabels.length-a.riskLabels.length))
const ranking=computed(()=>{const m=new Map<string,number>();for(const c of drawings.value.changes)for(const reason of new Set(c.reasons))m.set(reason,(m.get(reason)||0)+1);return [...m].map(([label,count])=>({label,count})).sort((a,b)=>b.count-a.count)})
const overTypes=computed(()=>{const m=new Map<string,{count:number;amount:number}>();for(const r of rows.value)for(const e of r.finance?.expenses||[])if(e.budget!=null&&e.actual!=null&&e.actual>e.budget){const item=m.get(e.category)||{count:0,amount:0};item.count++;item.amount+=e.actual-e.budget;m.set(e.category,item)}return [...m].map(([key,v])=>({key,label:expenseNames[key]||'其他费用',...v})).sort((a,b)=>b.amount-a.amount)})
const trends=computed(()=>{const result=[];for(let i=5;i>=0;i--){const date=new Date(`${to.value||day.value}T12:00:00+08:00`);date.setMonth(date.getMonth()-i,1);const month=`${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}`;const start=month+'-01',end=month+'-31';const input=data.value?buildReportingRows(data.value,day.value,start,end).filter(r=>rows.value.some(p=>p.id===r.id)):[];result.push({key:month,label:month.slice(5)+'月',changes:drawingStats(rows.value,start,end).changes.length,delivery:rate(input,'deliveryResult'),design:rate(input,'designResult')})}return result})
const filtered=computed(()=>{let items=rows.value.filter(r=>{const d=drill.value;if(!d)return true;if(d==='risk')return r.riskLabels.length;if(d==='over')return overruns.value.some(p=>p.id===r.id);if(d==='delivery')return r.deliveryResult.eligible;if(d==='design')return r.designResult.eligible;if(d==='drawing')return drawingStats([r],from.value,to.value).changed>0;if(d==='missing')return r.issues.length;if(d==='soon')return upcoming.value.some(p=>p.id===r.id);if(d.startsWith('risk:'))return r.riskLabels.includes(d.slice(5));if(d.startsWith('project:'))return r.id===d.slice(8);if(d.startsWith('quality:'))return !qualityFields.find(f=>f.key===d.slice(8))?.has(r);if(d.startsWith('month:'))return drawingStats([r],d.slice(6)+'-01',d.slice(6)+'-31').changes.length>0;if(d.startsWith('due-month:'))return (r.deliveryResult.eligible&&r.deliveryResult.due?.startsWith(d.slice(10)))||(r.designResult.eligible&&r.designResult.due?.startsWith(d.slice(10)));if(d.startsWith('date:'))return dueOf(r)===d.slice(5);if(d.startsWith('issue:'))return r.issues.includes(d.slice(6));if(d.startsWith('reason:'))return drawingStats([r],from.value,to.value).changes.some(c=>c.reasons.includes(d.slice(7)));if(d.startsWith('expense:'))return r.finance?.expenses.some(e=>e.category===d.slice(8)&&e.budget!=null&&e.actual!=null&&e.actual>e.budget);return true});return [...items].sort((a,b)=>sort.value==='amount'?(b.finance?.settlement??-1)-(a.finance?.settlement??-1):sort.value==='due'?(a.deliveryDate||'9999').localeCompare(b.deliveryDate||'9999'):sort.value==='days'?(b.stageDays??-1)-(a.stageDays??-1):a.code.localeCompare(b.code,'zh-CN'))})
const pages=computed(()=>Math.max(1,Math.ceil(filtered.value.length/10))),visible=computed(()=>filtered.value.slice((page.value-1)*10,page.value*10))
watch([unit,customer,manager,stage,search,drill,sort,from,to,days,dueKind],()=>page.value=1)
watch(pages,n=>page.value=Math.min(page.value,n))
function focus(kind:string,context='阶段分析'){drill.value=kind;tab.value=context;detailDialog.value?.showModal()}
function openProject(id:string){detailDialog.value?.close();emit('open',id,targetTab(tab.value))}
function closeOnBackdrop(event:MouseEvent){if(event.target===event.currentTarget)(event.currentTarget as HTMLDialogElement).close()}
const drillLabel=computed(()=>({risk:'重点风险',over:'实际超预算',delivery:'交货准时率样本',design:'设计准时率样本',drawing:'正式变更图纸所属项目',missing:'数据待完善',soon:'近期到期'}[drill.value]||drill.value.replace(/^(date|issue|reason|expense|risk|project|quality|month|due-month):/,'')))

const stageData=computed<ReportChartDatum[]>(()=>distribution.value.map(r=>({key:r.label,label:r.label,value:metric.value==='金额'?r.amount:distValue(r),color:r.color,hint:r.label+' · '+r.count+' 个项目 · '+r.customers+' 家客户 · 结算金额 '+money(r.amount)+' 元'})))
const trendData=computed(()=>trends.value.map(m=>({key:m.key,label:m.label,value:null})))
const trendSeries=computed(()=>[{label:'交货',color:colors[0]!,values:trends.value.map(m=>m.delivery.value),hints:trends.value.map(m=>m.key+' 交货准时率 '+percent(m.delivery.value)+'（'+m.delivery.onTime+'/'+m.delivery.total+'）')},{label:'设计',color:colors[1]!,values:trends.value.map(m=>m.design.value),hints:trends.value.map(m=>m.key+' 设计准时率 '+percent(m.design.value)+'（'+m.design.onTime+'/'+m.design.total+'）')}])
const riskData=computed(()=>['交货逾期','设计逾期','实际超预算','预计超预算','登记风险'].map((label,i)=>({key:label,label,value:rows.value.filter(r=>r.riskLabels.includes(label)).length,color:colors[i],hint:label+'：'+rows.value.filter(r=>r.riskLabels.includes(label)).length+' 个项目（项目可有多类风险）'})))
const budgetData=computed(()=>[{key:'budget',label:'预算',value:budget.value,color:colors[0]},{key:'actual',label:'实际',value:actual.value,color:colors[2]}])
const budgetRate=computed(()=>budget.value!=null&&budget.value>0&&actual.value!=null?actual.value/budget.value*100:null)
const overData=computed(()=>overTypes.value.slice(0,6).map(r=>({key:r.key,label:r.label,value:r.amount,color:colors[2],hint:r.label+'：超支 '+money(r.amount)+' 元 / '+r.count+' 个项目'})))
const changeData=computed(()=>trends.value.map(m=>({key:m.key,label:m.label,value:rows.value.some(r=>r.drawings!==null)?m.changes:null})))
const reasonData=computed(()=>ranking.value.slice(0,6).map((r,i)=>({key:r.label,label:r.label,value:r.count,color:colors[i%colors.length],hint:r.label+'：'+r.count+' 次（多选原因分别计次）'})))
const stageDuration=computed(()=>rows.value.filter(r=>r.stageDays!=null).map(r=>({key:r.id,label:r.name,group:r.stage,value:r.stageDays,color:distribution.value.find(s=>s.label===r.stage)?.color,hint:r.code+' '+r.name+' · '+r.stage+' '+r.stageDays+' 天'})))
const qualityFields=[{key:'settlement',label:'金额',has:(r:ReportRow)=>r.finance?.settlement!=null},{key:'budget',label:'成本',has:(r:ReportRow)=>r.finance?.budget!=null&&r.finance.actual!=null},{key:'delivery',label:'交货',has:(r:ReportRow)=>r.deliveryResult.status!=='unknown'},{key:'design',label:'设计',has:(r:ReportRow)=>r.designResult.status!=='unknown'},{key:'drawing',label:'图纸',has:(r:ReportRow)=>r.drawings!==null}]
const qualityData=computed(()=>distribution.value.map(s=>{const group=rows.value.filter(r=>r.stage===s.label);return {label:s.label,cells:qualityFields.map(f=>({...f,count:group.filter(f.has).length,total:group.length,value:group.filter(f.has).length/group.length*100}))}}))
const calendarOffset=computed(()=>new Date(day.value+'T12:00:00+08:00').getDay())
const heat=(value:number)=>({'--heat':Math.max(.06,Math.min(1,value/100)).toString()})
const maxDue=computed(()=>Math.max(1,...calendar.value.map(c=>c.count)))
function selectStage(value:string){stage.value=value;focus('')}
function selectQuality(value:string,key:string){stage.value=value;focus('quality:'+key)}
</script>

<template>
<section class="report-page" aria-label="项目经营总览" :aria-busy="loading">
 <div class="report-filters"><input v-model="search" type="search" placeholder="搜索项目" aria-label="搜索项目"><DepartmentSelectField :name="unit"><select v-model="unit" aria-label="事业部"><option value="">{{data?.canViewCompany?'公司全部事业部':'所属事业部'}}</option><option v-for="v in units" :key="v">{{v}}</option></select></DepartmentSelectField><CompanyNameField :name="customer"><select v-model="customer" aria-label="客户"><option value="">全部客户</option><option v-for="v in customers" :key="v">{{v}}</option></select></CompanyNameField><select v-model="manager" aria-label="负责人"><option value="">全部负责人</option><option v-for="v in managers" :key="v" :value="v">{{displayUserName(v)}}</option></select><select v-model="stage" aria-label="阶段筛选"><option value="">全部阶段</option><option v-for="v in stages" :key="v">{{v}}</option></select><label><input v-model="from" type="date" aria-label="统计开始日期"><span>—</span><input v-model="to" type="date" aria-label="统计结束日期" :max="day"></label><div class="report-actions"><button @click="methodDialog?.showModal()">统计口径</button><button @click="focus('')" :disabled="!data">项目明细</button><button :disabled="loading" @click="refresh" :title="data?'更新于 '+new Date(data.workbench.updatedAt).toLocaleString('zh-CN'):''">{{loading?'正在更新…':'刷新'}}</button></div></div>
 <p v-if="error||invalidDates" class="report-error" role="alert">{{error||'请选择有效的统计起止日期。'}}</p>
 <template v-if="data&&!invalidDates">
 <div class="report-kpis">
  <button type="button" class="pdm-stat-card report-kpi report-kpi--amount" @click="focus('')">
   <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon" aria-hidden="true"><Wallet :size="16" /></span>结算金额</span>
   <strong>{{settlement==null?'—':money(settlement)}}<small>元</small></strong>
   <em class="pdm-stat-card__detail">{{settlementCount}} / {{rows.length}} 个项目有数据</em>
  </button>
  <button type="button" class="pdm-stat-card report-kpi" @click="focus('')" title="按主项目去重，子项目不重复计入">
   <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon" aria-hidden="true"><FolderKanban :size="16" /></span>项目数</span>
   <strong>{{rows.length}}</strong>
  </button>
  <button type="button" class="pdm-stat-card report-kpi" @click="focus('')" title="按客户编码去重；未填写客户编码不计入">
   <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon" aria-hidden="true"><Building2 :size="16" /></span>客户数</span>
   <strong>{{customerCount}}</strong>
  </button>
  <button type="button" class="pdm-stat-card report-kpi" @click="focus('delivery','交付与设计')">
   <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon" aria-hidden="true"><CalendarCheck :size="16" /></span>交货准时率</span>
   <strong>{{percent(delivery.value)}}</strong>
   <em class="pdm-stat-card__detail">准时 {{delivery.onTime}} / 到期 {{delivery.total}}</em>
  </button>
  <button type="button" class="pdm-stat-card report-kpi" @click="focus('design','交付与设计')">
   <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon" aria-hidden="true"><PencilRuler :size="16" /></span>设计准时率</span>
   <strong>{{percent(design.value)}}</strong>
   <em class="pdm-stat-card__detail">准时 {{design.onTime}} / 到期 {{design.total}}</em>
  </button>
  <button type="button" class="pdm-stat-card report-kpi" :class="{'is-danger':overruns.length}" @click="focus('over','预算控制')">
   <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon" aria-hidden="true"><TriangleAlert :size="16" /></span>超预算项目</span>
   <strong>{{expenseSample.length?overruns.length:'—'}}</strong>
   <em class="pdm-stat-card__detail">{{expenseSample.length}} 个项目可比较</em>
  </button>
  <button type="button" class="pdm-stat-card report-kpi" @click="focus('drawing','图纸变更')">
   <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon" aria-hidden="true"><FilePenLine :size="16" /></span>图纸修改率</span>
   <strong>{{percent(drawings.value)}}</strong>
   <em class="pdm-stat-card__detail">变更 {{drawings.changed}} / 已发布 {{drawings.total}}</em>
  </button>
 </div>
 <div class="report-chart-grid">
  <section class="report-panel report-span-4" aria-label="项目阶段分布"><div class="report-panel-heading"><h2>项目阶段分布</h2><div class="report-segment"><button v-for="m in ['项目数','金额','客户数']" :key="m" :aria-pressed="metric===m" @click="metric=m">{{m}}</button></div></div><ProjectReportChart :active="active!==false" kind="columns" label="项目阶段分布" :data="stageData" @select="selectStage" /></section>
  <section class="report-panel report-span-5" aria-label="准时率趋势"><div class="report-panel-heading"><h2>准时率趋势 <small>近六个月</small></h2><div class="report-legend"><span :style="{'--color':colors[0]}">交货</span><span :style="{'--color':colors[1]}">设计</span></div></div><ProjectReportChart :active="active!==false" kind="line" label="交货与设计准时率趋势" :data="trendData" :series="trendSeries" percentage @select="focus('due-month:'+$event,'交付与设计')" /></section>
  <section class="report-panel report-span-3" aria-label="风险分布"><div class="report-panel-heading"><h2>风险分布</h2><span class="report-mini-value">{{risks.length}} 个项目</span></div><ProjectReportChart :active="active!==false" kind="donut" label="风险分布，按风险类型计项次" :data="riskData" :center="String(riskData.reduce((s,r)=>s+r.value,0))" caption="风险项次" @select="focus('risk:'+$event)" /></section>
  <section class="report-panel report-span-3" aria-label="预算执行"><div class="report-panel-heading"><h2>预算执行</h2><span class="report-mini-value">{{expenseSample.length}} 个样本 · 元</span></div><div class="report-budget-charts"><ProjectReportChart :active="active!==false" kind="gauge" label="预算执行率" :value="budgetRate" :color="budgetRate!=null&&budgetRate>100?'#ef6878':colors[2]" caption="实际 / 预算"/><ProjectReportChart :active="active!==false" kind="columns" label="预算与实际成本" :data="budgetData" @select="focus('','预算控制')" /></div></section>
  <section class="report-panel report-span-3" aria-label="超支类型"><div class="report-panel-heading"><h2>超支类型 <small>TOP 6</small></h2><span class="report-mini-value">元</span></div><ProjectReportChart :active="active!==false" kind="columns" label="分类超支金额排名" :data="overData" :color="colors[2]" @select="focus('expense:'+$event,'预算控制')" /></section>
  <section class="report-panel report-span-3" aria-label="图纸变更趋势"><div class="report-panel-heading"><h2>图纸变更趋势</h2><span class="report-mini-value">{{drawings.changes.length}} 次</span></div><ProjectReportChart :active="active!==false" kind="area" label="近六个月正式图纸变更次数" :data="changeData" :color="colors[3]" @select="focus('month:'+$event,'图纸变更')" /></section>
  <section class="report-panel report-span-3" aria-label="修改原因"><div class="report-panel-heading"><h2>修改原因 <small>TOP 6</small></h2><span class="report-mini-value" title="柱：原因次数；线：全部原因的累计占比">次数 / 累计%</span></div><ProjectReportChart :active="active!==false" kind="pareto" label="图纸修改原因排名与全部原因累计占比" :data="reasonData" :cumulative-total="ranking.reduce((s,r)=>s+r.count,0)" :color="colors[3]" @select="focus('reason:'+$event,'图纸变更')" /></section>
  <section class="report-panel report-span-4" aria-label="近期到期热力图"><div class="report-panel-heading"><h2>近期到期 <small>{{upcoming.length}} 项</small></h2><select v-model="dueKind" aria-label="到期类型"><option>交货</option><option>设计</option></select><div class="report-segment"><button v-for="n in [7,30]" :key="n" :aria-pressed="days===n" @click="days=n">{{n}}天</button></div></div><div class="report-calendar" data-chart="calendar"><span v-for="s in ['日','一','二','三','四','五','六']" :key="s" class="report-calendar-week">{{s}}</span><span v-for="n in calendarOffset" :key="'blank'+n"></span><button v-for="c in calendar" :key="c.date" class="report-heat-cell" :style="heat(c.count/maxDue*100)" :title="c.date+'：'+c.count+' 个'+dueKind+'项目'" :aria-label="c.date+'：'+c.count+' 个'+dueKind+'项目'" @click="focus('date:'+c.date)"><span>{{c.date.slice(8)}}</span><b v-if="c.count">{{c.count}}</b></button></div><div class="report-heat-legend"><span>{{day.slice(5)}} — {{addDays(day,days).slice(5)}}</span><span>少 <i v-for="n in [0,25,50,75,100]" :key="n" :style="heat(n)"></i> 多</span></div></section>
  <section class="report-panel report-span-4" aria-label="阶段停留分布"><div class="report-panel-heading"><h2>阶段停留分布</h2><span class="report-mini-value">{{stageDuration.length}} 个项目 · 天</span></div><ProjectReportChart :active="active!==false" kind="scatter" label="各项目当前阶段停留天数" :data="stageDuration" @select="focus('project:'+$event)" /></section>
  <section class="report-panel report-span-4" aria-label="数据完整性"><div class="report-panel-heading"><h2>数据完整性</h2><span class="report-mini-value">阶段 × 字段</span></div><div class="report-quality" data-chart="heatmap"><div class="report-quality-row report-quality-head"><span></span><span v-for="f in qualityFields" :key="f.key">{{f.label}}</span></div><div v-for="r in qualityData" :key="r.label" class="report-quality-row"><span :title="r.label">{{r.label}}</span><button v-for="c in r.cells" :key="c.key" class="report-heat-cell" :style="heat(c.value)" :title="r.label+' · '+c.label+'：'+c.count+'/'+c.total+' 个项目数据可用；点击查看缺项'" @click="selectQuality(r.label,c.key)">{{Math.round(c.value)}}%</button></div><div v-if="!qualityData.length" class="report-empty">暂无数据</div></div><div class="report-heat-legend"><span>可用数据占比</span><span>0% <i v-for="n in [0,25,50,75,100]" :key="n" :style="heat(n)"></i> 100%</span></div></section>
 </div>
 <dialog ref="detailDialog" class="report-dialog" aria-label="项目明细" @click="closeOnBackdrop"><header class="report-dialog-heading"><strong>项目明细</strong><button aria-label="关闭项目明细" @click="detailDialog?.close()">关闭</button></header><article id="report-detail" class="report-table-panel"><div class="report-card-title"><h2>项目明细 <span>{{filtered.length}} 个项目</span></h2><div class="report-table-tools"><button v-if="drill" @click="drill=''">{{drillLabel}} ×</button><select v-model="stage" aria-label="项目阶段"><option value="">全部阶段</option><option v-for="s in stages" :key="s">{{s}}</option></select><select v-model="sort" aria-label="项目排序"><option value="code">按项目编号</option><option value="amount">金额从高到低</option><option value="due">交期从近到远</option><option value="days">阶段停留从长到短</option></select></div></div><div class="report-table-scroll"><table><thead><tr><th>项目编号 / 名称</th><th>客户 / 事业部</th><th>阶段</th><th>结算金额（元）</th><th>交货 / 设计日期</th><th>负责人</th><th>关注事项</th><th>操作</th></tr></thead><tbody><tr v-for="r in visible" :key="r.id"><td><b>{{r.code}}</b><small>{{r.name}}</small></td><td><CompanyName :name="r.customer" /><small><DepartmentName :name="r.unit" /></small></td><td><span class="report-stage-label">{{r.stage}}</span><small v-if="r.stageDays!=null">{{r.stageDays}} 天</small></td><td>{{money(r.finance?.settlement)}}</td><td>{{r.deliveryDate||'待补充'}}<small>设计 {{r.designResult.due||'待补充'}}</small></td><td><PersonName :name="displayUserName(r.manager,'待分配')" /></td><td><span :class="{'report-danger':r.riskLabels.length}">{{r.riskLabels.join('、')||(r.issues.length?'数据待完善':'暂无异常')}}</span></td><td><button class="report-link" @click="openProject(r.id)">查看详情 →</button></td></tr><tr v-if="!visible.length"><td colspan="8" class="report-empty">暂无符合筛选条件的项目</td></tr></tbody></table></div><footer class="report-pagination"><span>第 {{page}} / {{pages}} 页 · 每页 10 条</span><div><button :disabled="page<=1" @click="page--">上一页</button><button :disabled="page>=pages" @click="page++">下一页</button></div></footer></article>
 </dialog>
 </template><div v-else-if="loading" class="report-empty" role="status">正在读取授权项目数据…</div>
 <dialog ref="methodDialog" class="report-dialog report-method-dialog" aria-label="统计口径" @click="closeOnBackdrop"><header class="report-dialog-heading"><strong>统计口径</strong><button @click="methodDialog?.close()">关闭</button></header><div class="report-method"><p>主项目按唯一编号汇总；金额采用主项目预算模块中的结算金额，不累加子项目以免重复。客户按客户编码去重，各阶段客户数不可直接相加。</p><p>交货以参与交期计算的生效计划任务全部完成为依据，优先采用项目约定交期；设计以设计阶段及设计审核节点全部完成为依据。实际完成日期缺失时不计准时；已到期未完成计入分母。取消、终止项目不计准时率。未设置交付节点的项目列为数据缺项。</p><p>预算只比较预算与实际成本均有值的同一批项目；缺失或无权查看不计为零。预计成本沿用预算模块算法。未授权项目不会由报表接口返回。</p><p>图纸仅统计正式发布的工程图；发布前存档不计修改，首次发布不计修改，同一正式修订重复发布去重。原因来自发布包，是UPLM统计机制；不代表逐张图纸均已独立登记原因。</p></div><p class="report-method">趋势图固定展示截至所选结束日期的最近六个月，空白月份不连线。风险按类型计次，项目可重复涉及不同风险；阶段散点仅包含可确认进入日期的项目。数据完整性表示当前账号可用数据比例，未授权与缺失均不计为可用。金额单位均为人民币元。帕累托折线以全部原因次数为分母，展示前六类的累计占比。</p></dialog>
</section>
</template>
<style scoped src="../styles/project-report.css"></style>
