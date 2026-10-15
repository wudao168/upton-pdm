import { expect, it } from 'vitest'
import { strFromU8, unzipSync } from 'three/examples/jsm/libs/fflate.module.js'
import { createOplWorkbook, oplExportHeaders } from '../src/oplWorkbook'
import type { ProjectExportMetadata } from '../src/exportFormat'

it('preserves full OPL content and numeric sequence under the unified project header', () => {
 const metadata:ProjectExportMetadata={title:'OPL开口问题清单',company:'测试公司',scope:'当前子项目',versionLabel:'数据时间',version:'2026-10-14',exportedAt:'2026-10-14T12:00:00+08:00',exportedBy:'王工',fields:['主项目号','主项目名称','型号','客户','子项目号','子项目名称','子项目型号','整套数量','事业部','项目经理','主设','工程师'].map((key,i)=>({key,value:i===4?'P700012-1':'项目资料'}))}
 const longText='完整描述与特殊字符<&>\n'+ '现场情况'.repeat(150)
 const files=unzipSync(createOplWorkbook([[27,'P700012-1','高','待处理',longText,'证据1.pdf\n证据2.png','完整处理过程','2026-10-21','王工','—','2026-10-14','李工']],metadata))
 const sheet=new DOMParser().parseFromString(strFromU8(files['xl/worksheets/sheet1.xml']!),'application/xml')
 expect(sheet.querySelector('parsererror')).toBeNull()
 expect([...sheet.querySelectorAll('row[r="7"] c')].map(c=>c.textContent)).toEqual(oplExportHeaders)
 expect(sheet.querySelector('c[r="E8"]')?.textContent).toBe(longText)
 expect(sheet.querySelector('c[r="F8"]')?.textContent).toBe('证据1.pdf\n证据2.png')
 expect(sheet.querySelector('c[r="A8"]')?.getAttribute('t')).toBeNull()
 expect(Number(sheet.querySelector('row[r="8"]')?.getAttribute('ht'))).toBeGreaterThan(22)
 expect(sheet.querySelector('autoFilter')?.getAttribute('ref')).toBe('A7:L8')
 expect(files['xl/media/uplm-company-logo.png']).toBeDefined()
})
