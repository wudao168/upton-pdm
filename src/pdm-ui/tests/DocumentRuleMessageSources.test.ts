import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { documentRuleMessageCatalog } from '../src/documentRuleMessages'

const sourceRoot = resolve(process.cwd(), '..')
const sourceText = ['Pdm.SolidWorks.Addin', 'Pdm.Application', 'Pdm.Infrastructure', 'Pdm.Domain', 'Pdm.Api', 'Pdm.CadArchiveWorker']
  .flatMap(directory => readdirSync(join(sourceRoot, directory)).filter(name => name.endsWith('.cs'))
    .map(name => readFileSync(join(sourceRoot, directory, name), 'utf8'))).join('\n')

describe('规则提示与实际源码对应', () => {
  for (const [id, info] of Object.entries(documentRuleMessageCatalog)) {
    for (const message of info.messages) {
      it(`${id} ${message.text}`, () => {
        const fragments = message.text.split(/\{[^}]+\}/).map(value => value.trim()).filter(value => value.length >= 4)
        expect(fragments.length).toBeGreaterThan(0)
        for (const fragment of fragments) expect(sourceText.includes(fragment), `提示原文已变化或未找到：${fragment}`).toBe(true)
      })
    }
  }
})
