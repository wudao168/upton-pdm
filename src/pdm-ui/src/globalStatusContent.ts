import libraryUrl from './data/globalStatusContent.json?url'

export type GlobalStatusContentCategory = 'tang' | 'songci' | 'quote' | 'holiday' | 'slogan'

export interface GlobalStatusContentItem {
  id: string
  category: GlobalStatusContentCategory
  text: string
  author?: string
  work?: string
  sourceFile?: number
  holidayKey?: string
}

interface ContentSourceFile {
  id: number
  path: string
  url: string
}

interface HolidayCalendar {
  year: number
  papers: string[]
  days: Array<{ name: string; date: string; isOffDay: boolean }>
}

export interface GlobalStatusContentLibrary {
  schemaVersion: number
  generatedAt: string
  counts: Record<GlobalStatusContentCategory, number>
  sources: {
    poetry: { name: string; url: string; commit: string; license: string; files: ContentSourceFile[] }
    holidays: { name: string; url: string; license: string }
    originals: { name: string }
  }
  holidayCalendars: HolidayCalendar[]
  items: GlobalStatusContentItem[]
}

export interface ResolvedGlobalStatusContent {
  item: GlobalStatusContentItem
  attribution: string
  sourceName: string
  sourceUrl?: string
  license: string
}

export const fallbackGlobalStatusContent: ResolvedGlobalStatusContent = {
  item: {
    id: 'fallback-system-original',
    category: 'slogan',
    text: '确认需求，让执行更准确。',
  },
  attribution: '系统原创',
  sourceName: '系统原创',
  license: '系统原创',
}

let libraryPromise: Promise<GlobalStatusContentLibrary> | undefined

export function loadGlobalStatusContent(): Promise<GlobalStatusContentLibrary> {
  libraryPromise ??= fetch(libraryUrl, { cache: 'force-cache' }).then(async response => {
    if (!response.ok) throw new Error(`顶部内容库加载失败：${response.status}`)
    const library = await response.json() as GlobalStatusContentLibrary
    const total = Object.values(library.counts).reduce((sum, value) => sum + value, 0)
    if (library.schemaVersion !== 1 || total !== 11100 || library.items.length !== 11100) {
      throw new Error('顶部内容库结构或数量不正确')
    }
    return library
  })
  return libraryPromise
}

function localDateKey(date: Date) {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function holidayForDate(library: GlobalStatusContentLibrary, now: Date) {
  const days = library.holidayCalendars.flatMap(calendar => calendar.days)
  const today = localDateKey(now)
  const offDay = days.find(day => day.date === today && day.isOffDay)
  if (offDay) return offDay.name
  if (now.getHours() < 15) return undefined
  const tomorrow = new Date(now)
  tomorrow.setDate(tomorrow.getDate() + 1)
  const tomorrowKey = localDateKey(tomorrow)
  return days.find(day => day.date === tomorrowKey && day.isOffDay)?.name
}

function attribution(item: GlobalStatusContentItem) {
  if (item.category === 'tang') return `唐·${item.author}《${item.work}》`
  if (item.category === 'songci') return `宋·${item.author}《${item.work}》`
  if (item.category === 'quote') return `《${item.author}${item.work ? `·${item.work}` : ''}》`
  if (item.category === 'holiday') return `系统原创·${item.holidayKey}祝福`
  return '系统原创'
}

export function resolveGlobalStatusContent(library: GlobalStatusContentLibrary, item: GlobalStatusContentItem): ResolvedGlobalStatusContent {
  if (item.sourceFile === undefined) {
    return { item, attribution: attribution(item), sourceName: library.sources.originals.name, license: '系统原创' }
  }
  const sourceFile = library.sources.poetry.files.find(file => file.id === item.sourceFile)
  return {
    item,
    attribution: attribution(item),
    sourceName: library.sources.poetry.name,
    sourceUrl: sourceFile?.url || library.sources.poetry.url,
    license: library.sources.poetry.license,
  }
}

export function selectGlobalStatusContent(library: GlobalStatusContentLibrary, now: Date): ResolvedGlobalStatusContent {
  const holiday = holidayForDate(library, now)
  if (holiday) {
    const holidayItems = library.items.filter(item => item.category === 'holiday' && item.holidayKey === holiday)
    if (holidayItems.length) {
      const dayIndex = Math.floor(new Date(localDateKey(now)).getTime() / 86_400_000)
      return resolveGlobalStatusContent(library, holidayItems[Math.abs(dayIndex) % holidayItems.length])
    }
  }
  const categories: GlobalStatusContentCategory[] = ['tang', 'quote', 'songci', 'slogan']
  const minute = Math.floor(now.getTime() / 60_000)
  const category = categories[Math.abs(minute) % categories.length]
  const items = library.items.filter(item => item.category === category)
  if (!items.length) return fallbackGlobalStatusContent
  const occurrence = Math.floor(Math.abs(minute) / categories.length)
  return resolveGlobalStatusContent(library, items[occurrence % items.length])
}

export function resetGlobalStatusContentCacheForTests() {
  libraryPromise = undefined
}
