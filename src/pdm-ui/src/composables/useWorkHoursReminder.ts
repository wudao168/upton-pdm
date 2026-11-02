import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { readWorkHours } from '../api'
import { chinaToday } from '../projectCalendar'
import { addDays } from '../projectWorkbench'
import type { WorkHoursView } from '../workHours'

type ReminderContext = { token: string; companyId: string; username: string }

/** 仅消费服务端的本人完整性检查；日期与应填名单、部门、假日规则保持一致。 */
export function useWorkHoursReminder(context: () => ReminderContext) {
  const count = ref(0)
  const key = (value: ReminderContext) => JSON.stringify([value.token, value.companyId, value.username.toLowerCase()])
  let generation = 0, disposed = false, timer: ReturnType<typeof setInterval> | undefined

  async function refresh() {
    const current = context(), requestKey = key(current), run = ++generation
    if (disposed || !current.token || !current.companyId || !current.username) { count.value = 0; return }
    const valid = () => !disposed && run === generation && requestKey === key(context())
    const missing = new Set<string>()
    const collect = (view: WorkHoursView) => {
      const own = view.compliance?.find(person => person.username.toLowerCase() === current.username.toLowerCase())
      if (own?.reminderEnabled !== true) return
      for (const day of own.days) {
        if (day.date < view.today && day.status === '未填写' && day.expected > 0 && day.actual === 0 && !day.muted) missing.add(day.date)
      }
    }
    try {
      const today = chinaToday()
      let from = addDays(today, -365)
      const view = await readWorkHours(from, today, false, current.token, true)
      if (!valid()) return
      collect(view)
      // 现有接口单次最多366天；历史欠填分段读取，始终与当前查看月份无关。
      const start = view.startDate
      while (start < from) {
        const to = addDays(from, -1)
        from = addDays(to, -365) < start ? start : addDays(to, -365)
        const earlier = await readWorkHours(from, to, false, current.token, true)
        if (!valid()) return
        collect(earlier)
      }
      count.value = missing.size
    } catch {
      // 后台计数读取失败时保留当前上下文的最后结果，下一次刷新重试。
    }
  }

  const refreshOnFocus = () => { void refresh() }
  watch(() => key(context()), () => { count.value = 0; void refresh() }, { immediate: true })
  onMounted(() => {
    timer = setInterval(() => { void refresh() }, 60_000)
    window.addEventListener('focus', refreshOnFocus)
  })
  onBeforeUnmount(() => {
    disposed = true; generation++
    if (timer !== undefined) clearInterval(timer)
    window.removeEventListener('focus', refreshOnFocus)
  })
  return { count, refresh }
}
