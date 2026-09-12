import { createHash } from 'node:crypto'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const toolRoot = dirname(fileURLToPath(import.meta.url))
const outputPath = resolve(toolRoot, '../src/data/globalStatusContent.json')
const cachePath = join(tmpdir(), 'upton-pdm-global-status-content')
const targets = { tang: 5000, songci: 5000, quote: 800, holiday: 100, slogan: 200 }
const upstream = 'https://github.com/chinese-poetry/chinese-poetry'
const apiRoot = 'https://api.github.com/repos/chinese-poetry/chinese-poetry'
const holidayUpstream = 'https://github.com/NateScarlet/holiday-cn'
const userAgent = 'UPTON-PDM-global-status-content-sync'
const forbidden = /(?:杀|弑|死|尸|亡|丧|墓|坟|血|病|疾|泣|妓|淫|盗|贼|战争|兵刃|仇敌|酷刑|自尽|醉|酗酒|帝王|皇帝|圣主|天子|蛮夷|鬼神|祭祀|宗庙)/
const classicBookNames = { daxue: '大学', mengzi: '孟子', zhongyong: '中庸' }

function hash(value) {
  return createHash('sha256').update(value).digest('hex')
}

function validExistingSnapshot() {
  if (!existsSync(outputPath)) return undefined
  try {
    const data = JSON.parse(readFileSync(outputPath, 'utf8'))
    const validCounts = Object.entries(targets).every(([key, value]) => data.counts?.[key] === value)
    const generatedAt = Date.parse(data.generatedAt)
    return validCounts && Number.isFinite(generatedAt) ? { data, generatedAt } : undefined
  } catch {
    return undefined
  }
}

const existing = validExistingSnapshot()
if (process.argv.includes('--if-stale') && existing && Date.now() - existing.generatedAt < 30 * 24 * 60 * 60 * 1000) {
  console.log(`顶部内容库仍在30天有效期内：${outputPath}`)
  process.exit(0)
}

async function fetchJson(url, optional = false) {
  mkdirSync(cachePath, { recursive: true })
  const cachedFile = join(cachePath, `${hash(url)}.json`)
  if (existsSync(cachedFile) && Date.now() - statSync(cachedFile).mtimeMs < 24 * 60 * 60 * 1000) {
    return JSON.parse(readFileSync(cachedFile, 'utf8'))
  }
  const temporaryFile = `${cachedFile}.download`
  const download = spawnSync('curl.exe', [
    '-fsSL', '--retry', '4', '--retry-all-errors', '--retry-delay', '2',
    '--connect-timeout', '30', '--max-time', '240',
    '-H', `User-Agent: ${userAgent}`, '-H', 'Accept: application/vnd.github+json, application/json',
    '-o', temporaryFile, url,
  ], { encoding: 'utf8', maxBuffer: 4 * 1024 * 1024 })
  if (download.status === 0) {
    try {
      const text = readFileSync(temporaryFile, 'utf8')
      const data = JSON.parse(text)
      rmSync(cachedFile, { force: true })
      renameSync(temporaryFile, cachedFile)
      return data
    } catch (error) {
      rmSync(temporaryFile, { force: true })
      if (!optional) throw new Error(`数据解析失败：${url}\n${error instanceof Error ? error.message : error}`)
      return undefined
    }
  }
  rmSync(temporaryFile, { force: true })
  if (optional) return undefined
  throw new Error(`下载失败：${url}\n${download.stderr || download.stdout || `curl退出码${download.status}`}`)
}

function evenlySpaced(items, count) {
  if (items.length <= count) return items
  return Array.from({ length: count }, (_, index) => items[Math.round(index * (items.length - 1) / (count - 1))])
}

async function concurrentMap(items, concurrency, mapper) {
  const result = new Array(items.length)
  let next = 0
  async function worker() {
    while (next < items.length) {
      const index = next++
      result[index] = await mapper(items[index], index)
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, items.length) }, worker))
  return result
}

function splitSentences(value) {
  const text = String(value || '').replace(/\s+/g, '').replace(/…+/g, '……')
  const matches = text.match(/[^。！？；]+[。！？；]/g) || []
  return matches.map(item => item.replace(/^[“”‘’「」『』]+|[“”‘’「」『』]+$/g, '').trim())
}

function chineseLength(value) {
  return (value.match(/[\u3400-\u9fff]/g) || []).length
}

function allowed(value) {
  const length = chineseLength(value)
  return length >= 8 && length <= 30 && !forbidden.test(value) && !/[□■◆●]|[A-Za-z]{3,}/.test(value)
}

function roundRobin(items, groupKey, limit) {
  const groups = new Map()
  for (const item of items) {
    const key = item[groupKey] || '未署名'
    if (!groups.has(key)) groups.set(key, [])
    groups.get(key).push(item)
  }
  const orderedGroups = [...groups.entries()]
    .sort(([left], [right]) => hash(left).localeCompare(hash(right)))
    .map(([, values]) => values.sort((left, right) => hash(`${left.text}|${left.work}`).localeCompare(hash(`${right.text}|${right.work}`))))
  const selected = []
  let depth = 0
  while (selected.length < limit) {
    let found = false
    for (const group of orderedGroups) {
      if (group[depth]) {
        selected.push(group[depth])
        found = true
        if (selected.length === limit) break
      }
    }
    if (!found) break
    depth += 1
  }
  return selected
}

function simplify(items) {
  const input = items.flatMap(item => [item.text, item.author || '', item.work || ''])
  const command = [
    '[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)',
    '[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)',
    'Add-Type -AssemblyName Microsoft.VisualBasic',
    '$values=[Console]::In.ReadToEnd() | ConvertFrom-Json',
    '$result=@($values | ForEach-Object { [Microsoft.VisualBasic.Strings]::StrConv([string]$_,[Microsoft.VisualBasic.VbStrConv]::SimplifiedChinese,2052) })',
    '$result | ConvertTo-Json -Compress',
  ].join(';')
  const converted = spawnSync('powershell.exe', ['-NoLogo', '-NoProfile', '-Command', command], {
    input: JSON.stringify(input), encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
  })
  if (converted.status !== 0) throw new Error(`繁简转换失败：${converted.stderr || converted.stdout}`)
  const values = JSON.parse(converted.stdout)
  return items.map((item, index) => ({
    ...item,
    text: values[index * 3],
    author: values[index * 3 + 1],
    work: values[index * 3 + 2],
  }))
}

function finalize(items, category, limit) {
  const simplified = simplify(roundRobin(items, 'author', limit * 2))
  const seen = new Set()
  const result = []
  for (const item of simplified) {
    const text = item.text.replace(/[“”‘’「」『』]/g, '').replaceAll('爲', '为').replaceAll('於', '于').replaceAll('後', '后')
    if (!allowed(text) || seen.has(text)) continue
    seen.add(text)
    result.push({
      id: `${category}-${hash(`${text}|${item.author}|${item.work}`).slice(0, 16)}`,
      category,
      text,
      author: item.author?.replaceAll('爲', '为').replaceAll('於', '于').replaceAll('後', '后') || undefined,
      work: item.work?.replaceAll('爲', '为').replaceAll('於', '于').replaceAll('後', '后') || undefined,
      sourceFile: item.sourceFile,
    })
    if (result.length === limit) break
  }
  if (result.length !== limit) throw new Error(`${category}筛选后只有${result.length}条，目标为${limit}条`)
  return result
}

function poetryCandidates(datasets, type, fileIndex) {
  const result = []
  for (const work of datasets.flat()) {
    if (!work?.author || !Array.isArray(work.paragraphs)) continue
    const title = type === 'tang' ? work.title : work.rhythmic
    if (!title || /无名氏|佚名/.test(work.author)) continue
    for (const paragraph of work.paragraphs) {
      for (const text of splitSentences(paragraph)) {
        if (allowed(text)) result.push({ text, author: work.author, work: title, sourceFile: fileIndex.get(work.__sourcePath) })
      }
    }
  }
  return result
}

function classicCandidates(datasets, fileIndex) {
  const result = []
  for (const { data, path, book } of datasets) {
    function visit(node, inheritedChapter = '') {
      if (Array.isArray(node)) {
        node.forEach(child => visit(child, inheritedChapter))
        return
      }
      if (!node || typeof node !== 'object') return
      const chapterName = node.chapter || node.title || node.name || inheritedChapter
      if (Array.isArray(node.paragraphs)) {
        for (const paragraph of node.paragraphs) {
          for (const text of splitSentences(paragraph)) {
          if (allowed(text) && !text.includes('？')) result.push({ text, author: book, work: chapterName, sourceFile: fileIndex.get(path) })
          }
        }
      }
      for (const [key, child] of Object.entries(node)) {
        if (key !== 'paragraphs') visit(child, chapterName)
      }
    }
    visit(data)
  }
  return result
}

const holidayTemplates = {
  元旦: {
    count: 15,
    openings: ['新岁启封', '元旦迎新', '岁序更新', '晨光启新', '新年新程'],
    endings: ['诸事顺遂。', '平安喜乐。', '一路向新。'],
  },
  春节: {
    count: 15,
    openings: ['新春纳福', '春启新程', '新春已至', '瑞岁迎春', '春风送暖'],
    endings: ['阖家安康。', '万事顺意。', '喜乐常伴。'],
  },
  清明节: {
    count: 14,
    openings: ['清明寄思', '清明春和', '清明时节', '春雨润心', '慎终追远', '草木清明', '春光和煦'],
    endings: ['珍惜当下。', '心怀感念。'],
  },
  劳动节: {
    count: 14,
    openings: ['致敬劳动', '礼赞创造', '劳动有光', '双手筑梦', '匠心耕耘', '认真成就', '奋斗正好'],
    endings: ['收获美好。', '成就未来。'],
  },
  端午节: {
    count: 14,
    openings: ['端午安康', '粽香端午', '端阳纳福', '艾香盈门', '仲夏端午', '龙舟竞渡', '端午如意'],
    endings: ['顺遂常伴。', '平安喜乐。'],
  },
  中秋节: {
    count: 14,
    openings: ['月满中秋', '桂香中秋', '清辉共赏', '花好月圆', '中秋团圆', '皓月当空', '月映美好'],
    endings: ['团圆安康。', '幸福常伴。'],
  },
  国庆节: {
    count: 14,
    openings: ['山河锦绣', '盛世华章', '金秋国庆', '家国同庆', '华夏欢歌', '江山如画', '举国欢庆'],
    endings: ['家国同庆。', '繁荣安康。'],
  },
}

function originalItems() {
  const holiday = []
  for (const [holidayKey, template] of Object.entries(holidayTemplates)) {
    const combinations = template.openings.flatMap(opening => template.endings.map(ending => `${opening}，${ending}`))
    combinations.slice(0, template.count).forEach((text, index) => holiday.push({
      id: `holiday-${holidayKey}-${String(index + 1).padStart(2, '0')}`,
      category: 'holiday', text, holidayKey,
    }))
  }
  const openings = ['确认需求', '明确责任', '核对数据', '校准参数', '规范命名', '保存记录', '验证结果', '关注细节', '及时沟通', '主动复盘', '尊重标准', '遵循流程', '提前准备', '持续改进', '协同配合', '控制变更', '记录依据', '重视安全', '守住质量', '面向现场']
  const endings = ['让执行更准确。', '让协作更顺畅。', '让结果可验证。', '让过程可追溯。', '让问题早发现。', '让交付更可靠。', '让现场少返工。', '让决策有依据。', '让质量看得见。', '让每一步都算数。']
  const slogan = openings.flatMap((opening, openingIndex) => endings.map((ending, endingIndex) => ({
    id: `slogan-${String(openingIndex * endings.length + endingIndex + 1).padStart(3, '0')}`,
    category: 'slogan', text: `${opening}，${ending}`,
  })))
  return { holiday, slogan }
}

async function buildSnapshot() {
  const commit = await fetchJson(`${apiRoot}/commits/master`)
  const commitSha = commit.sha
  const [tangDirectory, songDirectory, classicsDirectory] = await Promise.all([
    fetchJson(`${apiRoot}/contents/${encodeURIComponent('全唐诗')}?ref=${commitSha}`),
    fetchJson(`${apiRoot}/contents/${encodeURIComponent('宋词')}?ref=${commitSha}`),
    fetchJson(`${apiRoot}/contents/${encodeURIComponent('四书五经')}?ref=${commitSha}`),
  ])
  const tangFiles = evenlySpaced(tangDirectory.filter(file => /^poet\.tang\.\d+\.json$/.test(file.name)), 20)
  const songFiles = evenlySpaced(songDirectory.filter(file => /^ci\.song\.\d+\.json$/.test(file.name)), 12)
  const classicFiles = [
    { name: 'lunyu.json', path: '论语/lunyu.json', download_url: `https://raw.githubusercontent.com/chinese-poetry/chinese-poetry/${commitSha}/${encodeURIComponent('论语')}/lunyu.json`, book: '论语' },
    ...classicsDirectory.filter(file => file.type === 'file' && file.name.endsWith('.json')).map(file => {
      const key = file.name.replace(/\.json$/, '')
      return { ...file, book: classicBookNames[key] || key }
    }),
  ]
  const selectedFiles = [...tangFiles, ...songFiles, ...classicFiles]
  const files = selectedFiles.map((file, index) => ({
    id: index,
    path: file.path,
    url: `${upstream}/blob/${commitSha}/${file.path.split('/').map(encodeURIComponent).join('/')}`,
  }))
  const fileIndex = new Map(files.map(file => [file.path, file.id]))
  const loaded = await concurrentMap(selectedFiles, 2, async file => ({ file, data: await fetchJson(file.download_url) }))
  const tangData = loaded.filter(({ file }) => tangFiles.some(candidate => candidate.path === file.path)).map(({ file, data }) => data.map(item => ({ ...item, __sourcePath: file.path })))
  const songData = loaded.filter(({ file }) => songFiles.some(candidate => candidate.path === file.path)).map(({ file, data }) => data.map(item => ({ ...item, __sourcePath: file.path })))
  const classicsData = loaded.filter(({ file }) => classicFiles.some(candidate => candidate.path === file.path)).map(({ file, data }) => ({ data, path: file.path, book: file.book }))
  const tang = finalize(poetryCandidates(tangData, 'tang', fileIndex), 'tang', targets.tang)
  const songci = finalize(poetryCandidates(songData, 'songci', fileIndex), 'songci', targets.songci)
  const quote = finalize(classicCandidates(classicsData, fileIndex), 'quote', targets.quote)
  const originals = originalItems()
  const year = new Date().getFullYear()
  const holidayCalendars = (await Promise.all([year, year + 1].map(async calendarYear => {
    const data = await fetchJson(`https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/${calendarYear}.json`, true)
    return data ? { year: data.year, papers: data.papers, days: data.days } : undefined
  }))).filter(calendar => calendar?.days?.length)
  const items = [...tang, ...songci, ...quote, ...originals.holiday, ...originals.slogan]
  const counts = Object.fromEntries(Object.keys(targets).map(category => [category, items.filter(item => item.category === category).length]))
  if (Object.entries(targets).some(([category, expected]) => counts[category] !== expected)) throw new Error(`内容数量不正确：${JSON.stringify(counts)}`)
  return {
    schemaVersion: 1,
    generatedAt: new Date().toISOString(),
    counts,
    sources: {
      poetry: { name: 'chinese-poetry', url: upstream, commit: commitSha, license: 'MIT', files },
      holidays: { name: 'holiday-cn', url: holidayUpstream, license: 'MIT' },
      originals: { name: '系统原创' },
    },
    holidayCalendars,
    items,
  }
}

try {
  const snapshot = await buildSnapshot()
  mkdirSync(dirname(outputPath), { recursive: true })
  const temporaryPath = `${outputPath}.tmp`
  writeFileSync(temporaryPath, `${JSON.stringify(snapshot)}\n`, 'utf8')
  rmSync(outputPath, { force: true })
  renameSync(temporaryPath, outputPath)
  console.log(`顶部内容库已同步：${outputPath}`)
  console.log(`数量：${JSON.stringify(snapshot.counts)}，合计：${snapshot.items.length}`)
} catch (error) {
  if (existing) {
    console.warn(`同步失败，保留现有有效快照：${error instanceof Error ? error.message : error}`)
    process.exit(0)
  }
  throw error
}
