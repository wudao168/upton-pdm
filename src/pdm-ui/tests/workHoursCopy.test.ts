import { expect, it } from 'vitest'
import { appendWorkHoursDay, validateHours } from '../src/workHours'

it('追加保留原记录，按项目和地点累加，所有行写入目标日期且不改来源', () => {
  const existing = [{ date: '2026-10-20', projectId: 'a', location: '厂内', hours: 2 }, { date: '2026-10-20', projectId: 'b', location: '厂内', hours: 3 }]
  const copied = [{ date: '2026-10-19', projectId: 'a', location: '厂内', hours: 4 }, { date: '2026-10-19', projectId: 'a', location: '厂外', hours: 4 }]
  expect(appendWorkHoursDay(existing, copied, '2026-10-20')).toEqual([
    { date: '2026-10-20', projectId: 'a', location: '厂内', hours: 6 },
    existing[1], { date: '2026-10-20', projectId: 'a', location: '厂外', hours: 4 },
  ])
  expect(existing[0]?.hours).toBe(2)
  expect(copied[0]?.date).toBe('2026-10-19')
})

it('跨月复制使用目标日期，追加后的每日24小时限制继续生效', () => {
  const result = appendWorkHoursDay([{ date: '2026-09-30', projectId: 'a', location: '厂内', hours: 20 }],
    [{ date: '2026-10-19', projectId: 'b', location: '厂外', hours: 8 }], '2026-09-30')
  expect(result.every(entry => entry.date === '2026-09-30')).toBe(true)
  expect(validateHours(result)).toContain('24小时')
})
