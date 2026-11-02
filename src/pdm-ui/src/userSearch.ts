import { pinyin } from 'pinyin-pro'

const initials = new Map<string, string>()

export function matchesUserSearch(name: string, query: string, username = '') {
  const normalized = query.trim().toLocaleLowerCase('zh-CN')
  if (!normalized || `${name} ${username}`.toLocaleLowerCase('zh-CN').includes(normalized)) return true
  if (!/^[a-z\s]+$/.test(normalized)) return false
  let index = initials.get(name)
  if (index === undefined) {
    index = pinyin(name, { pattern: 'first', toneType: 'none', surname: 'head', separator: '' }).toLowerCase()
    initials.set(name, index)
  }
  return index.includes(normalized.replace(/\s+/g, ''))
}
