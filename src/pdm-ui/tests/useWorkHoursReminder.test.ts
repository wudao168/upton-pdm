import { defineComponent, nextTick, reactive } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import * as api from '../src/api'
import * as calendar from '../src/projectCalendar'
import { useWorkHoursReminder } from '../src/composables/useWorkHoursReminder'
import type { WorkHoursDayCheck, WorkHoursView } from '../src/workHours'

const wrappers: Array<{ unmount: () => void }> = []
const day = (date: string, extra: Partial<WorkHoursDayCheck> = {}): WorkHoursDayCheck => ({ date, expected: 8, actual: 0, status: '未填写', muted: false, reason: '', ...extra })
const view = (days = [day('2026-10-26')], enabled = true, startDate = '2026-10-01'): WorkHoursView => ({ today: '2026-10-31', startDate, username: 'designer', displayName: '设计人员', scope: '仅本人', people: [], projects: [], sheets: [], compliance: [{ username: 'DESIGNER', reminderEnabled: enabled, days }] })
function setup(initial = { token: 'token', companyId: 'company-a', username: 'designer' }) {
  const context = reactive(initial)
  let reminder!: ReturnType<typeof useWorkHoursReminder>
  wrappers.push(mount(defineComponent({ setup() { reminder = useWorkHoursReminder(() => context); return () => null } })))
  return { context, reminder }
}
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
  vi.spyOn(calendar, 'chinaToday').mockReturnValue('2026-10-31')
  vi.spyOn(api, 'readWorkHours').mockResolvedValue(view())
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.restoreAllMocks(); vi.useRealTimers() })

it('只计本人需要提醒的整日未填工作日，排除不足、豁免、静默及今天未来日期', async () => {
  const days = [
    day('2026-10-26'), day('2026-10-26'), day('2026-10-27', { actual: 4, status: '填写不完整' }),
    day('2026-10-28', { expected: 0, status: '已豁免' }), day('2026-10-29', { muted: true }), day('2026-10-31'), day('2026-11-01'),
  ]
  vi.mocked(api.readWorkHours).mockResolvedValue({ ...view(days), compliance: [...view(days).compliance!, { username: 'other', reminderEnabled: true, days: [day('2026-10-30')] }] })
  const { reminder } = setup(); await flushPromises()
  expect(reminder.count.value).toBe(1)
  expect(api.readWorkHours).toHaveBeenCalledWith('2025-10-31', '2026-10-31', false, 'token', true)
})

it('未登录时不请求工时，登录后开始读取', async () => {
  const { context, reminder } = setup({ token: '', companyId: 'company-a', username: 'designer' })
  await flushPromises(); expect(api.readWorkHours).not.toHaveBeenCalled(); expect(reminder.count.value).toBe(0)
  context.token = 'token'; await nextTick(); await flushPromises()
  expect(reminder.count.value).toBe(1)
})

it('未加入当前应填名单、部门未参与或无应填日期时不显示计数', async () => {
  vi.mocked(api.readWorkHours).mockResolvedValue(view([day('2026-10-26')], false))
  const { reminder } = setup(); await flushPromises(); expect(reminder.count.value).toBe(0)
  vi.mocked(api.readWorkHours).mockResolvedValue({ ...view(), compliance: [] })
  await reminder.refresh(); expect(reminder.count.value).toBe(0)
})

it('按最多366天分段读取全部生效历史，累计跨年欠填且不重叠', async () => {
  vi.mocked(api.readWorkHours)
    .mockResolvedValueOnce(view([day('2026-10-26')], true, '2024-10-01'))
    .mockResolvedValueOnce(view([day('2025-05-02')], true, '2024-10-01'))
    .mockResolvedValueOnce(view([day('2024-10-15')], true, '2024-10-01'))
  const { reminder } = setup(); await flushPromises()
  expect(reminder.count.value).toBe(3)
  expect(vi.mocked(api.readWorkHours).mock.calls.map(call => call.slice(0, 2))).toEqual([
    ['2025-10-31', '2026-10-31'], ['2024-10-30', '2025-10-30'], ['2024-10-01', '2024-10-29'],
  ])
})

it('切换账号或公司立即清空，旧请求不能覆盖新上下文', async () => {
  let finish!: (value: WorkHoursView) => void
  vi.mocked(api.readWorkHours).mockImplementationOnce(() => new Promise(resolve => { finish = resolve })).mockResolvedValue(view([day('2026-10-27'), day('2026-10-28')]))
  const { context, reminder } = setup()
  context.companyId = 'company-b'; await nextTick(); await flushPromises()
  expect(reminder.count.value).toBe(2)
  finish(view([day('2026-10-26')])); await flushPromises(); expect(reminder.count.value).toBe(2)
  context.token = ''; await nextTick(); expect(reminder.count.value).toBe(0)
})

it('保存后主动刷新会采用最新结果，较早返回的数据不会覆盖', async () => {
  let finish!: (value: WorkHoursView) => void
  vi.mocked(api.readWorkHours).mockImplementationOnce(() => new Promise(resolve => { finish = resolve })).mockResolvedValue(view([]))
  const { reminder } = setup(); await reminder.refresh(); expect(reminder.count.value).toBe(0)
  finish(view()); await flushPromises(); expect(reminder.count.value).toBe(0)
})

it('每分钟和回到窗口时刷新，临时失败保留当前公司最后结果', async () => {
  const { reminder } = setup(); await flushPromises(); expect(reminder.count.value).toBe(1)
  vi.mocked(api.readWorkHours).mockResolvedValue(view([day('2026-10-26'), day('2026-10-27')]))
  await vi.advanceTimersByTimeAsync(60_000); expect(reminder.count.value).toBe(2)
  vi.mocked(api.readWorkHours).mockRejectedValue(new Error('网络暂时中断'))
  window.dispatchEvent(new Event('focus')); await flushPromises(); expect(reminder.count.value).toBe(2)
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  const calls = vi.mocked(api.readWorkHours).mock.calls.length
  await vi.advanceTimersByTimeAsync(60_000); window.dispatchEvent(new Event('focus')); await flushPromises()
  expect(api.readWorkHours).toHaveBeenCalledTimes(calls)
})
