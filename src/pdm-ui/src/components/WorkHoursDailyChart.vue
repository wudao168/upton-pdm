<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{ active: boolean; title?: string; selectable?: boolean; days: { date: string; label?: string; description?: string; inside: number; outside: number }[] }>(), { title: '每日工时', selectable: false })
const emit = defineEmits<{ select: [id: string] }>()
const format = (value: number) => Number(value.toFixed(2)).toLocaleString('zh-CN')
const total = computed(() => props.days.reduce((sum, day) => sum + day.inside + day.outside, 0))
// 四等分刻度，向上取整到易读的小时值，确保柱体不超出轨道。
const step = computed(() => {
  const raw = Math.max(8, ...props.days.map(day => day.inside + day.outside)) / 4
  const magnitude = 10 ** Math.floor(Math.log10(raw))
  return ([1, 2, 2.5, 5, 10].find(value => value * magnitude >= raw) ?? 10) * magnitude
})
const maximum = computed(() => step.value * 4)
const ticks = computed(() => [4, 3, 2, 1, 0].map(value => format(value * step.value)))
const dataKey = computed(() => props.days.map(day => `${day.date}:${day.inside}:${day.outside}`).join('|'))
</script>

<template>
  <section class="wh-daily-chart" :aria-label="`${title}统计`">
    <div class="wh-daily-chart__heading">
      <div><h3>{{ title }}</h3><p><strong>{{ format(total) }}</strong><span>小时</span></p></div>
      <div class="wh-daily-chart__legend"><span><i class="inside"></i>厂内</span><span><i class="outside"></i>厂外</span></div>
    </div>
    <div class="wh-daily-chart__plot">
      <div class="wh-daily-chart__axis" aria-hidden="true"><span v-for="tick in ticks" :key="tick">{{ tick }}</span></div>
      <p v-if="!days.length" class="wh-daily-chart__empty">当前范围暂无工时记录</p>
      <div v-else :key="`${active}:${dataKey}`" class="wh-daily-chart__bars" :role="selectable ? 'group' : 'list'" :aria-label="`${title}厂内与厂外工时，单位小时`">
        <component :is="selectable ? 'button' : 'div'" v-for="(day, index) in days" :key="day.date" class="wh-daily-chart__column" :class="{ 'wh-bar-column': selectable }" :role="selectable ? undefined : 'listitem'" :type="selectable ? 'button' : undefined" tabindex="0" @click="selectable && emit('select', day.date)"
          :aria-label="selectable ? `查看${day.label}项目工时明细` : `${day.date}，厂内${format(day.inside)}小时，厂外${format(day.outside)}小时，合计${format(day.inside + day.outside)}小时`">
          <div class="wh-daily-chart__track">
            <div class="wh-daily-chart__fill" :style="{ height: `${(day.inside + day.outside) / maximum * 100}%`, '--bar-delay': `${Math.min(index * 35, 420)}ms` }">
              <div class="outside" :style="{ flexGrow: day.outside }"></div><div class="inside" :style="{ flexGrow: day.inside }"></div>
            </div>
            <div class="wh-daily-chart__tooltip">{{ day.description || day.date }}<br>厂内 {{ format(day.inside) }} · 厂外 {{ format(day.outside) }} 小时</div>
          </div>
          <small>{{ day.label || day.date.slice(5) }}</small>
        </component>
      </div>
    </div>
  </section>
</template>

<style scoped>
@layer pdm-button-sizing {
  .wh-daily-chart {
    --chart-track: color-mix(in srgb, var(--pdm-text) 7%, var(--wh-calendar-board));
    box-sizing: border-box; min-width: 0; padding: 18px 20px 14px !important;
    border: 0 !important; border-radius: 16px !important; background: var(--wh-calendar-board) !important;
  }

  .wh-daily-chart__heading { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; margin-bottom: 24px; }
  .wh-daily-chart h3 { margin: 0; font-size: 12px; font-weight: 500; color: var(--pdm-muted); }
  .wh-daily-chart p { display: flex; align-items: baseline; gap: 6px; margin: 4px 0 0; }
  .wh-daily-chart p strong { font-size: 26px; line-height: 1.25; font-weight: 600; color: var(--pdm-text); font-variant-numeric: tabular-nums; }
  .wh-daily-chart p span { font-size: 12px; color: var(--pdm-muted); }
  .wh-daily-chart__legend { display: flex; gap: 12px; font-size: 11px; color: var(--pdm-muted); }
  .wh-daily-chart__legend span { display: flex; gap: 5px; align-items: center; }
  .wh-daily-chart__legend i { width: 7px; height: 7px; border-radius: 50%; }
  .wh-daily-chart .inside { background: #10b981; }
  .wh-daily-chart .outside { background: #f97316; }
  .wh-daily-chart__plot { display: flex; gap: 12px; height: 208px; }
  .wh-daily-chart__axis { flex: 0 0 34px; display: flex; flex-direction: column; justify-content: space-between; padding-bottom: 28px; font-size: 10px; color: var(--pdm-muted); text-align: right; }
  .wh-daily-chart__bars { display: flex; flex: 1; min-width: 0; gap: 14px; overflow-x: auto; padding: 0 3px 4px; }
  .wh-daily-chart__column { flex: 1 0 30px; display: flex; align-items: center; flex-direction: column; gap: 8px; min-width: 30px; outline: none; }
  button.wh-daily-chart__column { border:0 !important; padding:0 !important; border-radius:0 !important; background:transparent !important; min-width:60px !important; min-height:0 !important; height:auto !important; justify-content:flex-start !important; cursor:pointer; }
  .wh-daily-chart__empty { flex:1; display:grid; place-items:center; color:var(--pdm-muted); font-size:12px; }
  .wh-daily-chart__track { position: relative; width: 100%; max-width: 34px; flex: 1; min-height: 0; border-radius: 9px; background: var(--chart-track); }
  .wh-daily-chart__fill { position: absolute; bottom: 0; width: 100%; display: flex; flex-direction: column; border-radius: 9px; overflow: hidden; transform-origin: bottom; animation: wh-daily-bar-rise 900ms cubic-bezier(.22,1,.36,1) var(--bar-delay) both; }
  .wh-daily-chart__fill > div { flex-basis: 0; min-height: 0; }
  .wh-daily-chart__column small { font-size: 10px; line-height: 16px; color: var(--pdm-muted); white-space: nowrap; }
  .wh-daily-chart__tooltip { display: none; position: absolute; z-index: 2; bottom: 8px; left: 50%; transform: translateX(-50%); width: max-content; max-width: 180px; padding: 6px 8px; border: 1px solid var(--pdm-border); border-radius: 8px; background: var(--pdm-bg); color: var(--pdm-text); font-size: 11px; line-height: 18px; pointer-events: none; box-shadow: 0 3px 12px #0002; }
  .wh-daily-chart__column:first-child .wh-daily-chart__tooltip { left: 0; transform: none; }
  .wh-daily-chart__column:last-child .wh-daily-chart__tooltip { left: auto; right: 0; transform: none; }
  .wh-daily-chart__column:hover .wh-daily-chart__tooltip, .wh-daily-chart__column:focus-visible .wh-daily-chart__tooltip { display: block; }
  .wh-daily-chart__column:focus-visible .wh-daily-chart__track { outline: 2px solid #10b981; outline-offset: 2px; }
  @media (prefers-reduced-motion: reduce) { .wh-daily-chart__fill { animation: none; } }
}
@keyframes wh-daily-bar-rise { from { transform: scaleY(0); } to { transform: scaleY(1); } }
</style>
