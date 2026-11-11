import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElNotification } from 'element-plus'
import { listProjectActivities } from '../api'
import { currentActivities, type ProjectActivity } from '../projectActivity'

export function useProjectActivityFeed(token: () => string, scope: () => string, open: (item: ProjectActivity) => void) {
  const items = ref<ProjectActivity[]>([])
  let generation = 0, initial = true, busy = false
  let controller: AbortController | undefined
  let seen = new Set<string>()
  let notification: ReturnType<typeof ElNotification> | undefined
  let timer: ReturnType<typeof setInterval> | undefined
  const key = () => 'pdm.project-activity.seen:' + scope()
  function saveSeen() {
    try { sessionStorage.setItem(key(), JSON.stringify([...seen].slice(-500))) } catch { /* 存储受限仍可轮播 */ }
  }
  async function refresh() {
    if (!token() || busy || document.hidden) return
    const current = generation
    busy = true
    controller = new AbortController()
    try {
      const data = currentActivities(await listProjectActivities(token(), controller.signal))
      if (current !== generation) return
      const fresh = data.filter(item => item.important && !seen.has(item.id))
      items.value = data
      data.forEach(item => seen.add(item.id))
      saveSeen()
      if (!initial && fresh.length) {
        const item = fresh[0]!
        notification?.close()
        notification = ElNotification({
          title: item.projectCode + '｜' + item.title,
          message: fresh.length > 1 ? item.content + '（另有 ' + (fresh.length - 1) + ' 条动态，顶部可查看）' : item.content,
          position: 'bottom-right', duration: 6000, type: 'info',
          onClick: () => { notification?.close(); open(item) },
        })
      }
      initial = false
    } catch (error) {
      if (current === generation) {
        items.value = []
        if (!controller?.signal.aborted) console.warn('项目动态刷新未完成', error)
      }
    } finally { if (current === generation) busy = false }
  }
  watch([token, scope], () => {
    generation++; controller?.abort(); notification?.close()
    items.value = []; initial = true; busy = false; seen = new Set()
    try { seen = new Set(JSON.parse(sessionStorage.getItem(key()) || '[]')) } catch { /* 忽略旧缓存 */ }
    void refresh()
  }, { immediate: true })
  function visible() { if (!document.hidden) void refresh() }
  onMounted(() => {
    timer = setInterval(() => { items.value = currentActivities(items.value); void refresh() }, 30000)
    document.addEventListener('visibilitychange', visible)
  })
  onBeforeUnmount(() => {
    generation++; controller?.abort(); notification?.close()
    if (timer) clearInterval(timer)
    document.removeEventListener('visibilitychange', visible)
  })
  return { items, refresh }
}
