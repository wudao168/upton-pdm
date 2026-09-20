import { describe, expect, it } from 'vitest'
import { repairMojibake } from '../src/mojibake'

const windows1252Characters = new Map<number, string>([
  [0x80, '\u20ac'], [0x82, '\u201a'], [0x83, '\u0192'], [0x84, '\u201e'], [0x85, '\u2026'], [0x86, '\u2020'], [0x87, '\u2021'],
  [0x88, '\u02c6'], [0x89, '\u2030'], [0x8a, '\u0160'], [0x8b, '\u2039'], [0x8c, '\u0152'], [0x8e, '\u017d'], [0x91, '\u2018'],
  [0x92, '\u2019'], [0x93, '\u201c'], [0x94, '\u201d'], [0x95, '\u2022'], [0x96, '\u2013'], [0x97, '\u2014'], [0x98, '\u02dc'],
  [0x99, '\u2122'], [0x9a, '\u0161'], [0x9b, '\u203a'], [0x9c, '\u0153'], [0x9e, '\u017e'], [0x9f, '\u0178'],
])

function utf8Bytes(value: string) {
  return Array.from(new TextEncoder().encode(value))
}

// UTF-8 字节被按 Windows-1252 读出。
function mojibakeWindows1252(value: string) {
  return utf8Bytes(value)
    .map(byte => byte <= 0x7f ? String.fromCharCode(byte) : windows1252Characters.get(byte) ?? String.fromCharCode(byte))
    .join('')
}

// UTF-8 字节被按 ISO-8859-1 读出，0x80-0x9F 会变成 C1 控制字符。
function mojibakeLatin1(value: string) {
  return utf8Bytes(value).map(byte => String.fromCharCode(byte)).join('')
}

const releaseNote = '完善BOM提前发布、工程套件和图纸二维码功能。'

describe('repairMojibake', () => {
  it('还原单轮和多轮 Windows-1252 乱码', () => {
    let corrupted = releaseNote
    for (let round = 1; round <= 6; round += 1) {
      corrupted = mojibakeWindows1252(corrupted)
      expect(repairMojibake(corrupted)).toBe(releaseNote)
    }
  })

  it('还原按 ISO-8859-1 解读产生的乱码', () => {
    let corrupted = releaseNote
    for (let round = 1; round <= 6; round += 1) {
      corrupted = mojibakeLatin1(corrupted)
      expect(repairMojibake(corrupted)).toBe(releaseNote)
    }
  })

  it('还原两种编码混合多轮的乱码', () => {
    let corrupted = releaseNote
    for (let round = 0; round < 4; round += 1) {
      corrupted = round % 2 === 0 ? mojibakeLatin1(corrupted) : mojibakeWindows1252(corrupted)
      expect(repairMojibake(corrupted)).toBe(releaseNote)
    }
  })

  it('还原测试服务器上真实的版本记录乱码', () => {
    const corrupted = '\u00C3\u00A5\u00C2\u00AE\u00C2\u008C\u00C3\u00A5\u00C2\u0096\u00C2\u0084BOM\u00C3\u00A6\u00C2\u008F\u00C2\u0090\u00C3\u00A5\u00C2\u0089\u00C2\u008D\u00C3\u00A5\u00C2\u008F\u00C2\u0091\u00C3\u00A5\u00C2\u00B8\u00C2\u0083\u00C3\u00A3\u00C2\u0080\u00C2\u0081\u00C3\u00A5\u00C2\u00B7\u00C2\u00A5\u00C3\u00A7\u00C2\u00A8\u00C2\u008B\u00C3\u00A5\u00C2\u00A5\u00C2\u0097\u00C3\u00A4\u00C2\u00BB\u00C2\u00B6\u00C3\u00A5\u00C2\u0092\u00C2\u008C\u00C3\u00A5\u00C2\u009B\u00C2\u00BE\u00C3\u00A7\u00C2\u00BA\u00C2\u00B8\u00C3\u00A4\u00C2\u00BA\u00C2\u008C\u00C3\u00A7\u00C2\u00BB\u00C2\u00B4\u00C3\u00A7\u00C2\u00A0\u00C2\u0081\u00C3\u00A5\u00C2\u008A\u00C2\u009F\u00C3\u00A8\u00C2\u0083\u00C2\u00BD\u00C3\u00A3\u00C2\u0080\u00C2\u0082'
    expect(repairMojibake(corrupted)).toBe(releaseNote)
  })

  it('正常文本保持不变', () => {
    expect(repairMojibake(releaseNote)).toBe(releaseNote)
    expect(repairMojibake('release note 2026.09.20')).toBe('release note 2026.09.20')
    expect(repairMojibake('')).toBe('')
  })
})
