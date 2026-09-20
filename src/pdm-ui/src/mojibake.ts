const windows1252Bytes = new Map<number, number>([
  [0x20ac, 0x80], [0x201a, 0x82], [0x0192, 0x83], [0x201e, 0x84], [0x2026, 0x85], [0x2020, 0x86], [0x2021, 0x87],
  [0x02c6, 0x88], [0x2030, 0x89], [0x0160, 0x8a], [0x2039, 0x8b], [0x0152, 0x8c], [0x017d, 0x8e], [0x2018, 0x91],
  [0x2019, 0x92], [0x201c, 0x93], [0x201d, 0x94], [0x2022, 0x95], [0x2013, 0x96], [0x2014, 0x97], [0x02dc, 0x98],
  [0x2122, 0x99], [0x0161, 0x9a], [0x203a, 0x9b], [0x0153, 0x9c], [0x017e, 0x9e], [0x0178, 0x9f],
])

function toByteValues(value: string) {
  const bytes: number[] = []
  for (const character of value) {
    const codePoint = character.codePointAt(0) ?? 0
    if (codePoint <= 0xff) bytes.push(codePoint)
    else if (windows1252Bytes.has(codePoint)) bytes.push(windows1252Bytes.get(codePoint)!)
    else return null
  }
  return bytes
}

// 单字节被当作 Windows-1252 字符读出后，只有合法 UTF-8 序列能还原；其余字节保持原样，避免整轮放弃。
function decodeUtf8(bytes: number[]) {
  const decoder = new TextDecoder('utf-8', { fatal: true })
  let text = ''
  let index = 0
  while (index < bytes.length) {
    const lead = bytes[index]!
    const length = lead >= 0xf0 ? 4 : lead >= 0xe0 ? 3 : lead >= 0xc2 ? 2 : 1
    if (length > 1 && index + length <= bytes.length) {
      try {
        text += decoder.decode(Uint8Array.from(bytes.slice(index, index + length)))
        index += length
        continue
      } catch {
        // 该位置不是完整 UTF-8 序列，退回单字节。
      }
    }
    text += String.fromCharCode(lead)
    index += 1
  }
  return text
}

/**
 * 还原被反复以 Windows-1252 解读 UTF-8 字节造成的乱码。
 * 历史发布记录可能被污染多轮，因此逐轮还原直到不再变化。
 */
export function repairMojibake(value: string, maxAttempts = 6) {
  let current = value
  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    if (/[\u3400-\u9fff]/u.test(current)) break
    const bytes = toByteValues(current)
    if (!bytes) break
    const decoded = decodeUtf8(bytes)
    if (decoded === current) break
    current = decoded
  }
  return current
}
