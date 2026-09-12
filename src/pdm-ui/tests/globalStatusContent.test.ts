import { describe, expect, it } from 'vitest'
import libraryJson from '../src/data/globalStatusContent.json'
import {
  holidayForDate,
  resolveGlobalStatusContent,
  selectGlobalStatusContent,
  type GlobalStatusContentLibrary,
} from '../src/globalStatusContent'

const library = libraryJson as unknown as GlobalStatusContentLibrary

describe('global status content library', () => {
  it('contains the confirmed 11,100 sourced entries', () => {
    expect(library.counts).toEqual({ tang: 5000, songci: 5000, quote: 800, holiday: 100, slogan: 200 })
    expect(library.items).toHaveLength(11100)
    expect(new Set(library.items.map(item => item.id)).size).toBe(11100)
    expect(library.sources.poetry.commit).toMatch(/^[0-9a-f]{40}$/)
    expect(library.sources.poetry.license).toBe('MIT')
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

  it('rotates the four normal categories once per minute', () => {
    const categories = [0, 1, 2, 3].map(minute => selectGlobalStatusContent(library, new Date(minute * 60_000)).item.category)
    expect(categories).toEqual(['tang', 'quote', 'songci', 'slogan'])
  })
})
