import { describe, expect, it } from 'vitest'
import libraryJson from '../src/data/globalStatusContent.json'
import {
  festivalForDate,
  holidayForDate,
  resolveGlobalStatusContent,
  selectGlobalStatusContent,
  type GlobalStatusContentLibrary,
} from '../src/globalStatusContent'

const library = libraryJson as unknown as GlobalStatusContentLibrary

describe('global status content library', () => {
  it('contains the confirmed 11,220 sourced entries', () => {
    expect(library.counts).toEqual({ tang: 5000, songci: 5000, quote: 800, holiday: 220, slogan: 200 })
    expect(library.items).toHaveLength(11220)
    expect(new Set(library.items.map(item => item.id)).size).toBe(11220)
    expect(library.sources.poetry.commit).toMatch(/^[0-9a-f]{40}$/)
    expect(library.sources.poetry.license).toBe('MIT')
  })

  it('keeps a greeting for every festival the status bar can show', () => {
    const holidayKeys = new Set(library.items.filter(item => item.category === 'holiday').map(item => item.holidayKey))
    expect(holidayKeys.size).toBe(19)
    for (const key of ['元旦', '春节', '除夕', '元宵节', '清明节', '劳动节', '端午节', '七夕节', '中秋节', '重阳节', '腊八节', '妇女节', '植树节', '青年节', '儿童节', '建党节', '建军节', '教师节', '国庆节']) {
      expect(holidayKeys.has(key)).toBe(true)
    }
  })

  it('keeps attribution and source links for imported content', () => {
    const item = library.items.find(candidate => candidate.category === 'tang')!
    const resolved = resolveGlobalStatusContent(library, item)
    expect(item.author).toBeTruthy()
    expect(item.work).toBeTruthy()
    expect(resolved.attribution).toMatch(/^唐·.+《.+》$/)
    expect(resolved.sourceName).toBe('chinese-poetry')
    expect(resolved.sourceUrl).toContain(library.sources.poetry.commit)
  })

  it('uses official holiday dates from 15:00 on the preceding day', () => {
    expect(holidayForDate(library, new Date(2025, 11, 31, 14, 59))).toBeUndefined()
    expect(holidayForDate(library, new Date(2025, 11, 31, 15, 0))).toBe('元旦')
    const selected = selectGlobalStatusContent(library, new Date(2026, 0, 1, 9, 0))
    expect(selected.item.category).toBe('holiday')
    expect(selected.item.holidayKey).toBe('元旦')
    expect(selected.sourceName).toBe('系统原创')
  })

  it('greets lunar festivals that the official holiday list does not cover', () => {
    expect(holidayForDate(library, new Date(2026, 0, 26, 9, 0))).toBe('腊八节')
    expect(holidayForDate(library, new Date(2026, 2, 3, 9, 0))).toBe('元宵节')
    expect(holidayForDate(library, new Date(2026, 7, 19, 9, 0))).toBe('七夕节')
    expect(holidayForDate(library, new Date(2026, 9, 18, 9, 0))).toBe('重阳节')
    expect(holidayForDate(library, new Date(2027, 1, 6, 9, 0))).toBe('除夕')
    const selected = selectGlobalStatusContent(library, new Date(2026, 2, 3, 9, 0))
    expect(selected.item.category).toBe('holiday')
    expect(selected.item.holidayKey).toBe('元宵节')
    expect(selected.attribution).toBe('系统原创·元宵节祝福')
  })

  it('greets solar festivals on their fixed dates', () => {
    expect(holidayForDate(library, new Date(2026, 2, 12, 9, 0))).toBe('植树节')
    expect(holidayForDate(library, new Date(2026, 8, 10, 9, 0))).toBe('教师节')
    expect(holidayForDate(library, new Date(2027, 5, 1, 9, 0))).toBe('儿童节')
    expect(holidayForDate(library, new Date(2026, 8, 9, 9, 0))).toBeUndefined()
    expect(holidayForDate(library, new Date(2026, 8, 9, 15, 0))).toBe('教师节')
    expect(holidayForDate(library, new Date(2026, 6, 15, 9, 0))).toBeUndefined()
  })

  it('keeps the official holiday name when both sources cover the same day', () => {
    expect(festivalForDate(new Date(2026, 4, 4, 9, 0))).toBe('青年节')
    expect(holidayForDate(library, new Date(2026, 4, 4, 9, 0))).toBe('劳动节')
  })

  it('rotates the four normal categories once per minute', () => {
    const baseMinute = Math.ceil(new Date(2026, 6, 15, 12, 0).getTime() / 60_000 / 4) * 4
    const categories = [0, 1, 2, 3].map(offset => selectGlobalStatusContent(library, new Date((baseMinute + offset) * 60_000)).item.category)
    expect(categories).toEqual(['tang', 'quote', 'songci', 'slogan'])
  })
})
